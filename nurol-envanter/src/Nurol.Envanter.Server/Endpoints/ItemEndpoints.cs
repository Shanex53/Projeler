using System.Text;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Server.Services;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

public record ItemQuery(string? Q, Guid? CategoryId, Guid? WarehouseId, ItemStatus? Status, bool? MissingDocument,
    Guid? FieldId, string? FieldValue, string? Sort, bool Desc, int Page, int PageSize, Guid? EmployeeId = null);

public static class ItemEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/items", async (HttpContext ctx, AppDbContext db, string? q, Guid? categoryId, Guid? warehouseId, ItemStatus? status,
            bool? missingDocument, Guid? fieldId, string? fieldValue, string? sort, bool? desc, int? page, int? pageSize) =>
        {
            var a = await ctx.Access();
            var query = new ItemQuery(q, categoryId, warehouseId, status, missingDocument, fieldId, fieldValue, sort, desc ?? false, Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 50, 1, 500));
            var (rows, total) = await ListAsync(db, a, query);
            return new PagedResult<ItemListRow>(rows, total, query.Page, query.PageSize);
        });

        api.MapGet("/items/export", async (HttpContext ctx, AppDbContext db, string? q, Guid? categoryId, Guid? warehouseId, ItemStatus? status, bool? missingDocument, Guid? fieldId, string? fieldValue, string? sort, bool? desc) =>
        {
            var a = await ctx.Access();
            var (rows, _) = await ListAsync(db, a, new ItemQuery(q, categoryId, warehouseId, status, missingDocument, fieldId, fieldValue, sort, desc ?? false, 1, 100_000));
            var fields = await db.FieldDefinitions.Where(f => !f.Archived).OrderBy(f => f.Label).ToListAsync();
            var used = fields.Where(f => rows.Any(r => r.Fields.ContainsKey(f.Key))).ToList();
            var sb = new StringBuilder();
            string Esc(string? s) => s is null ? "" : s.Contains(';') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
            sb.AppendLine(string.Join(';', new[] { "Envanter etiketi", "Kategori", "Marka", "Model", "Seri no", "Durum", "Depo", "Lokasyon", "Zimmetli personel", "İmzalı evrak", "Maliyet" }.Concat(used.Select(f => f.Label)).Select(Esc)));
            foreach (var r in rows)
            {
                var cols = new[] { r.Tag, r.CategoryName, r.ManufacturerName, r.ModelName, r.Serial, Labels.Status(r.Status), r.WarehouseName, r.LocationName, r.EmployeeName,
                    r.SignedDocumentMissing switch { true => "Eksik", false => "Yüklendi", _ => "" }, r.Cost?.ToString("0.00") }
                    .Concat(used.Select(f => r.Fields.GetValueOrDefault(f.Key)));
                sb.AppendLine(string.Join(';', cols.Select(Esc)));
            }
            Audit.Add(db, a, "Dışa aktarım", "Envanter", null, $"{rows.Count} kayıt");
            await db.SaveChangesAsync();
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", $"envanter-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
        });

        api.MapGet("/items/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            return await DetailAsync(db, a, id);
        });

        api.MapGet("/items/by-tag/{tag}", async (string tag, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var id = await a.Items(db).Where(i => i.Tag == tag.ToUpper()).Select(i => (Guid?)i.Id).FirstOrDefaultAsync() ?? throw AppException.NotFound("Envanter");
            return Results.Ok(new { id });
        });

        api.MapPost("/items", async (ItemSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            a.RequireWriteWarehouse(req.WarehouseId);
            var model = await db.AssetModels.Include(m => m.Category).FirstOrDefaultAsync(m => m.Id == req.AssetModelId && !m.Archived)
                ?? throw AppException.Invalid("Geçerli bir model seçin.", new() { ["model"] = "Model seçin." });
            if (model.Category!.Archived) throw AppException.Invalid("Arşivlenmiş kategoriye kayıt eklenemez.");
            var wh = await db.Warehouses.Include(w => w.Company).FirstOrDefaultAsync(w => w.Id == req.WarehouseId && !w.Archived)
                ?? throw AppException.Invalid("Geçerli bir depo seçin.", new() { ["warehouse"] = "Depo seçin." });
            await ValidateLocation(db, req.LocationId, wh.Id);
            var serial = NormalizeSerial(req.Serial);
            if (model.Category.RequiresSerial && serial is null)
                throw AppException.Invalid("Seri numarası zorunludur.", new() { ["serial"] = "Bu kategori için seri numarası zorunludur." });
            await EnsureSerialFree(db, model.ManufacturerId, serial, null);

            await using var tx = await db.Database.BeginTransactionAsync();
            var item = await CreateAsync(db, a, model, wh, req.LocationId, serial, req.Cost, Trim(req.Notes), req.Fields ?? [], "Envantere eklendi");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new { item.Id, item.Tag });
        });

        api.MapPut("/items/{id:guid}", async (Guid id, ItemSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var item = await a.Items(db).FirstOrDefaultAsync(i => i.Id == id) ?? throw AppException.NotFound("Envanter");
            a.RequireWriteWarehouse(item.WarehouseId);
            if (req.Version is uint v) db.Entry(item).Property(x => x.Version).OriginalValue = v;
            if (req.WarehouseId != item.WarehouseId)
                throw AppException.Conflict("Depo değişikliği transfer ile yapılır (Transferler sayfası). Lokasyonu bu ekrandan değiştirebilirsiniz.");
            var model = await db.AssetModels.Include(m => m.Category).FirstOrDefaultAsync(m => m.Id == req.AssetModelId)
                ?? throw AppException.Invalid("Geçerli bir model seçin.");
            if (model.CategoryId != item.CategoryId) throw AppException.Conflict("Kaydın kategorisi değiştirilemez; etiket kategoriye bağlıdır.");
            await ValidateLocation(db, req.LocationId, item.WarehouseId);
            var serial = NormalizeSerial(req.Serial);
            if (model.Category!.RequiresSerial && serial is null)
                throw AppException.Invalid("Seri numarası zorunludur.", new() { ["serial"] = "Bu kategori için seri numarası zorunludur." });
            await EnsureSerialFree(db, model.ManufacturerId, serial, item.Id);

            await using var tx = await db.Database.BeginTransactionAsync();
            var changes = new List<string>();
            if (item.Serial != serial) changes.Add($"Seri no: {item.Serial ?? "—"} → {serial ?? "—"}");
            if (item.AssetModelId != model.Id) changes.Add("Model değişti");
            if (item.LocationId != req.LocationId) changes.Add("Lokasyon değişti");
            item.AssetModelId = model.Id; item.ManufacturerId = model.ManufacturerId; item.Serial = serial;
            item.LocationId = req.LocationId; item.Cost = req.Cost; item.Notes = Trim(req.Notes);
            var fields = await FieldRules.ActiveFieldsAsync(db, item.CategoryId, model.Id);
            await FieldRules.ApplyAsync(db, item, fields, req.Fields ?? []);
            Event(db, item.Id, "Düzenleme", changes.Count > 0 ? string.Join(" · ", changes) : "Bilgiler güncellendi", a.UserName);
            Audit.Add(db, a, "Güncelleme", "Envanter", item.Id, item.Tag);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.NoContent();
        });

        api.MapPost("/items/{id:guid}/maintenance", (Guid id, MaintenanceRequest req, HttpContext ctx, AppDbContext db) =>
            ChangeStatus(id, req, ctx, db, ItemStatus.InStock, ItemStatus.InMaintenance, "Bakım", "Bakıma alındı"));
        api.MapPost("/items/{id:guid}/maintenance/end", (Guid id, MaintenanceRequest req, HttpContext ctx, AppDbContext db) =>
            ChangeStatus(id, req, ctx, db, ItemStatus.InMaintenance, ItemStatus.InStock, "Bakım", "Bakımdan döndü"));
        api.MapPost("/items/{id:guid}/retire", (Guid id, MaintenanceRequest req, HttpContext ctx, AppDbContext db) =>
            ChangeStatus(id, req, ctx, db, ItemStatus.InStock, ItemStatus.Retired, "Kullanım dışı", "Kullanım dışı bırakıldı"));
        api.MapPost("/items/{id:guid}/reactivate", (Guid id, MaintenanceRequest req, HttpContext ctx, AppDbContext db) =>
            ChangeStatus(id, req, ctx, db, ItemStatus.Retired, ItemStatus.InStock, "Kullanım dışı", "Tekrar kullanıma alındı"));
    }

    static async Task<IResult> ChangeStatus(Guid id, MaintenanceRequest req, HttpContext ctx, AppDbContext db, ItemStatus from, ItemStatus to, string type, string text)
    {
        var a = await ctx.Access();
        var item = await a.Items(db).FirstOrDefaultAsync(i => i.Id == id) ?? throw AppException.NotFound("Envanter");
        a.RequireWriteWarehouse(item.WarehouseId);
        db.Entry(item).Property(x => x.Version).OriginalValue = req.Version;
        if (item.Status != from)
            throw AppException.Conflict($"Cihaz şu an \"{Labels.Status(item.Status)}\" durumunda; bu işlem yalnızca \"{Labels.Status(from)}\" durumundaki cihazlara uygulanır.");
        if (to != ItemStatus.InStock && string.IsNullOrWhiteSpace(req.Note))
            throw AppException.Invalid("Açıklama zorunludur.", new() { ["note"] = "Neden bu işlemin yapıldığını yazın." });
        item.Status = to;
        item.MaintenanceNote = to == ItemStatus.InMaintenance ? req.Note.Trim() : null;
        Event(db, item.Id, type, string.IsNullOrWhiteSpace(req.Note) ? text : $"{text}: {req.Note.Trim()}", a.UserName);
        Audit.Add(db, a, text, "Envanter", item.Id, item.Tag);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    internal static async Task<(List<ItemListRow>, int)> ListAsync(AppDbContext db, Access a, ItemQuery q)
    {
        var items = a.Items(db).AsNoTracking();
        if (q.CategoryId is Guid c) items = items.Where(i => i.CategoryId == c);
        if (q.WarehouseId is Guid w) items = items.Where(i => i.WarehouseId == w);
        if (q.Status is ItemStatus s) items = items.Where(i => i.Status == s);
        if (q.EmployeeId is Guid emp) items = items.Where(i => i.EmployeeId == emp);
        if (q.MissingDocument == true)
            items = items.Where(i => i.OpenAssignmentId != null && !db.Documents.Any(d => d.AssignmentId == i.OpenAssignmentId && d.Kind == DocumentKind.Signed));
        if (q.FieldId is Guid fid && !string.IsNullOrWhiteSpace(q.FieldValue))
        {
            var fv = q.FieldValue.Trim().ToLower();
            items = items.Where(i => db.FieldValues.Any(v => v.ItemId == i.Id && v.FieldId == fid && v.Text!.ToLower().Contains(fv)));
        }
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var t = $"%{q.Q.Trim()}%";
            items = items.Where(i => EF.Functions.ILike(i.Tag, t) || EF.Functions.ILike(i.Serial ?? "", t) ||
                EF.Functions.ILike(i.AssetModel!.Name, t) || EF.Functions.ILike(i.AssetModel.Manufacturer!.Name, t) ||
                (i.Employee != null && EF.Functions.ILike(i.Employee.FullName, t)) ||
                db.FieldValues.Any(v => v.ItemId == i.Id && EF.Functions.ILike(v.Text ?? "", t)));
        }
        var total = await items.CountAsync();

        IOrderedQueryable<InventoryItem> ordered;
        var sort = q.Sort ?? "tag";
        if (sort.StartsWith("f:") && Guid.TryParse(sort[2..], out var sortField))
        {
            var type = await db.FieldDefinitions.Where(f => f.Id == sortField).Select(f => f.Type).FirstOrDefaultAsync();
            ordered = type switch
            {
                FieldType.Number => q.Desc
                    ? items.OrderByDescending(i => db.FieldValues.Where(v => v.ItemId == i.Id && v.FieldId == sortField).Select(v => v.Number).FirstOrDefault())
                    : items.OrderBy(i => db.FieldValues.Where(v => v.ItemId == i.Id && v.FieldId == sortField).Select(v => v.Number).FirstOrDefault()),
                FieldType.Date => q.Desc
                    ? items.OrderByDescending(i => db.FieldValues.Where(v => v.ItemId == i.Id && v.FieldId == sortField).Select(v => v.Date).FirstOrDefault())
                    : items.OrderBy(i => db.FieldValues.Where(v => v.ItemId == i.Id && v.FieldId == sortField).Select(v => v.Date).FirstOrDefault()),
                _ => q.Desc
                    ? items.OrderByDescending(i => db.FieldValues.Where(v => v.ItemId == i.Id && v.FieldId == sortField).Select(v => v.Text).FirstOrDefault())
                    : items.OrderBy(i => db.FieldValues.Where(v => v.ItemId == i.Id && v.FieldId == sortField).Select(v => v.Text).FirstOrDefault())
            };
        }
        else
        {
            ordered = (sort, q.Desc) switch
            {
                ("category", false) => items.OrderBy(i => i.Category!.Name),
                ("category", true) => items.OrderByDescending(i => i.Category!.Name),
                ("model", false) => items.OrderBy(i => i.AssetModel!.Manufacturer!.Name).ThenBy(i => i.AssetModel!.Name),
                ("model", true) => items.OrderByDescending(i => i.AssetModel!.Manufacturer!.Name).ThenByDescending(i => i.AssetModel!.Name),
                ("serial", false) => items.OrderBy(i => i.Serial),
                ("serial", true) => items.OrderByDescending(i => i.Serial),
                ("status", false) => items.OrderBy(i => i.Status),
                ("status", true) => items.OrderByDescending(i => i.Status),
                ("warehouse", false) => items.OrderBy(i => i.Warehouse!.Name),
                ("warehouse", true) => items.OrderByDescending(i => i.Warehouse!.Name),
                ("employee", false) => items.OrderBy(i => i.Employee!.FullName),
                ("employee", true) => items.OrderByDescending(i => i.Employee!.FullName),
                ("cost", false) => items.OrderBy(i => i.Cost),
                ("cost", true) => items.OrderByDescending(i => i.Cost),
                (_, true) => items.OrderByDescending(i => i.Tag),
                _ => items.OrderBy(i => i.Tag)
            };
        }
        var page = await ordered.ThenBy(i => i.Tag).Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .Select(i => new
            {
                i.Id, i.Tag, Category = i.Category!.Name, Manufacturer = i.AssetModel!.Manufacturer!.Name, Model = i.AssetModel.Name, i.Serial,
                i.Status, i.WarehouseId, Warehouse = i.Warehouse!.Name, Location = i.Location != null ? i.Location.Name : null,
                Employee = i.Employee != null ? i.Employee.FullName : null, i.OpenAssignmentId,
                Missing = i.OpenAssignmentId == null ? (bool?)null : !db.Documents.Any(d => d.AssignmentId == i.OpenAssignmentId && d.Kind == DocumentKind.Signed),
                i.Cost
            }).ToListAsync();

        var ids = page.Select(p => p.Id).ToList();
        var values = await db.FieldValues.Where(v => ids.Contains(v.ItemId) && !v.Field!.Archived)
            .Select(v => new { v.ItemId, v.Field!.Key, v.Field.Type, v.Text }).ToListAsync();
        var rows = page.Select(p => new ItemListRow(p.Id, p.Tag, p.Category, p.Manufacturer, p.Model, p.Serial, p.Status, p.WarehouseId, p.Warehouse, p.Location,
            p.Employee, p.Missing, p.Cost,
            values.Where(v => v.ItemId == p.Id).ToDictionary(v => v.Key, v => (string?)FieldRules.Display(v.Type, v.Text)))).ToList();
        return (rows, total);
    }

    internal static async Task<ItemDetail> DetailAsync(AppDbContext db, Access a, Guid id)
    {
        var i = await a.Items(db).AsNoTracking()
            .Include(x => x.Company).Include(x => x.Category).Include(x => x.AssetModel!).ThenInclude(m => m.Manufacturer)
            .Include(x => x.Warehouse).Include(x => x.Location).Include(x => x.Employee).Include(x => x.OpenAssignment)
            .FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("Envanter");

        var active = await FieldRules.ActiveFieldsAsync(db, i.CategoryId, i.AssetModelId);
        var values = await db.FieldValues.Where(v => v.ItemId == id).Include(v => v.Field).ToListAsync();
        var fields = active.Select(f => new FieldValueDto(f.Id, f.Key, f.Label, f.Type, values.FirstOrDefault(v => v.FieldId == f.Id)?.Text, false))
            .Concat(values.Where(v => active.All(f => f.Id != v.FieldId)).Select(v => new FieldValueDto(v.FieldId, v.Field!.Key, v.Field.Label, v.Field.Type, v.Text, true)))
            .ToList();

        var history = await db.ItemEvents.Where(e => e.ItemId == id).OrderByDescending(e => e.At)
            .Select(e => new HistoryDto(e.At, e.Type, e.Text, e.UserName, e.Reference)).ToListAsync();
        var assignmentIds = await db.AssignmentLines.Where(l => l.ItemId == id).Select(l => l.AssignmentId).ToListAsync();
        var docs = await db.Documents.Where(d => assignmentIds.Contains(d.AssignmentId)).OrderByDescending(d => d.At)
            .Select(d => new DocumentDto(d.Id, d.AssignmentId, d.Assignment!.No, d.Kind, d.Version, d.FileName, d.ContentType, d.Size, d.Sha256, d.At, d.UserName)).ToListAsync();

        return new ItemDetail(i.Id, i.Tag, i.CompanyId, i.Company!.Name, i.CategoryId, i.Category!.Name, i.Category.RequiresSerial,
            i.AssetModelId, i.AssetModel!.Manufacturer!.Name, i.AssetModel.Name, i.Serial, i.Status,
            i.WarehouseId, i.Warehouse!.Name, i.LocationId, i.Location?.Name, i.EmployeeId, i.Employee?.FullName,
            i.OpenAssignmentId, i.OpenAssignment?.No, i.Cost, i.Notes, i.MaintenanceNote, i.Version, fields, history, docs);
    }

    /// <summary>Yeni tekil kayıt: şirket ve kategori koduna göre etiket üretir, özel alanları doğrular. SaveChanges çağırana aittir.</summary>
    internal static async Task<InventoryItem> CreateAsync(AppDbContext db, Access a, AssetModel model, Warehouse wh, Guid? locationId, string? serial,
        decimal? cost, string? notes, IReadOnlyDictionary<Guid, string?> fieldValues, string eventText)
    {
        var n = await Counters.NextAsync(db, $"TAG-{wh.Company!.Code}-{model.Category!.Code}");
        var item = new InventoryItem
        {
            Id = Guid.NewGuid(),
            Tag = $"{wh.Company.Code}-{model.Category.Code}-{n:000000}",
            CompanyId = wh.CompanyId, CategoryId = model.CategoryId, ManufacturerId = model.ManufacturerId, AssetModelId = model.Id,
            WarehouseId = wh.Id, LocationId = locationId, Serial = serial, Status = ItemStatus.InStock,
            Cost = cost, Notes = notes, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Items.Add(item);
        var fields = await FieldRules.ActiveFieldsAsync(db, model.CategoryId, model.Id);
        await FieldRules.ApplyAsync(db, item, fields, fieldValues);
        Event(db, item.Id, "Kayıt", $"{eventText}: {wh.Name}", a.UserName);
        Audit.Add(db, a, "Oluşturma", "Envanter", item.Id, item.Tag);
        return item;
    }

    internal static void Event(AppDbContext db, Guid itemId, string type, string text, string user, string? reference = null) =>
        db.ItemEvents.Add(new ItemEvent { Id = Guid.NewGuid(), ItemId = itemId, At = DateTimeOffset.UtcNow, Type = type, Text = text, UserName = user, Reference = reference });

    static async Task ValidateLocation(AppDbContext db, Guid? locationId, Guid warehouseId)
    {
        if (locationId is Guid l && !await db.Locations.AnyAsync(x => x.Id == l && x.WarehouseId == warehouseId && !x.Archived))
            throw AppException.Invalid("Lokasyon seçilen depoya ait değil.", new() { ["location"] = "Depoya ait bir lokasyon seçin." });
    }

    static async Task EnsureSerialFree(AppDbContext db, Guid manufacturerId, string? serial, Guid? self)
    {
        if (serial is null) return;
        var other = await db.Items.Where(i => i.ManufacturerId == manufacturerId && i.Serial == serial && i.Id != self).Select(i => i.Tag).FirstOrDefaultAsync();
        if (other is not null)
            throw AppException.Conflict($"Bu seri numarası aynı markanın başka bir cihazında ({other}) kayıtlı.");
    }

    internal static string? NormalizeSerial(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();
    static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

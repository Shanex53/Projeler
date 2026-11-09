using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Server.Services;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

public record HandoverRequest(Guid FromEmployeeId, Guid ToEmployeeId, IReadOnlyList<Guid> ItemIds, string? Note);

public static class AssignmentEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/assignments", async (HttpContext ctx, AppDbContext db, string? q, Guid? employeeId, AssignmentKind? kind, bool? missingSigned, int? page, int? pageSize) =>
        {
            var a = await ctx.Access();
            var query = a.Assignments(db).AsNoTracking();
            if (employeeId is Guid e) query = query.Where(x => x.EmployeeId == e);
            if (kind is AssignmentKind k) query = query.Where(x => x.Kind == k);
            if (missingSigned == true) query = query.Where(x => !x.Documents.Any(d => d.Kind == DocumentKind.Signed));
            if (!string.IsNullOrWhiteSpace(q))
            {
                var t = $"%{q.Trim()}%";
                query = query.Where(x => EF.Functions.ILike(x.No, t) || EF.Functions.ILike(x.Employee!.FullName, t) ||
                    x.Lines.Any(l => EF.Functions.ILike(l.Item!.Tag, t)));
            }
            var p = Math.Max(1, page ?? 1); var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var total = await query.CountAsync();
            var rows = await Project(query.OrderByDescending(x => x.At).Skip((p - 1) * size).Take(size)).ToListAsync();
            return new PagedResult<AssignmentListRow>(rows, total, p, size);
        });

        api.MapGet("/assignments/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            return await Detail(db, a, id);
        });

        api.MapPost("/assignments", async (AssignmentCreate req, HttpContext ctx, AppDbContext db, IFileStore files) =>
        {
            var a = await ctx.Access();
            var key = ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (!string.IsNullOrEmpty(key) && await db.Assignments.Where(x => x.IdempotencyKey == key).Select(x => (Guid?)x.Id).FirstOrDefaultAsync() is Guid prev)
                return Results.Ok(await Detail(db, a, prev));
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var id = await Issue(db, files, a, req.EmployeeId, req.ItemIds, req.ConditionNotes, req.Note, key);
                await tx.CommitAsync();
                return Results.Ok(await Detail(db, a, id));
            }
            catch (DbUpdateException ex) when (Idempotency.IsKeyViolation(ex))
            {
                db.ChangeTracker.Clear();
                var existing = await db.Assignments.Where(x => x.IdempotencyKey == key).Select(x => x.Id).FirstAsync();
                return Results.Ok(await Detail(db, a, existing));
            }
        });

        api.MapPost("/assignments/return", async (AssignmentReturn req, HttpContext ctx, AppDbContext db, IFileStore files) =>
        {
            var a = await ctx.Access();
            var key = ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (!string.IsNullOrEmpty(key) && await db.Assignments.Where(x => x.IdempotencyKey == key).Select(x => (Guid?)x.Id).FirstOrDefaultAsync() is Guid prev)
                return Results.Ok(await Detail(db, a, prev));
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var id = await Return(db, files, a, req.EmployeeId, req.ItemIds, req.LocationId, req.ConditionNotes, req.Note, key);
                await tx.CommitAsync();
                return Results.Ok(await Detail(db, a, id));
            }
            catch (DbUpdateException ex) when (Idempotency.IsKeyViolation(ex))
            {
                db.ChangeTracker.Clear();
                var existing = await db.Assignments.Where(x => x.IdempotencyKey == key).Select(x => x.Id).FirstAsync();
                return Results.Ok(await Detail(db, a, existing));
            }
        });

        // Devir: iade ve yeni zimmet tek işlemde; iki ayrı tutanak üretilir.
        api.MapPost("/assignments/handover", async (HandoverRequest req, HttpContext ctx, AppDbContext db, IFileStore files) =>
        {
            var a = await ctx.Access();
            if (req.FromEmployeeId == req.ToEmployeeId) throw AppException.Invalid("Devir için farklı bir personel seçin.");
            var key = ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (!string.IsNullOrEmpty(key) && await db.Assignments.Where(x => x.IdempotencyKey == key + ":zimmet").Select(x => (Guid?)x.Id).FirstOrDefaultAsync() is Guid prev)
                return Results.Ok(await Detail(db, a, prev));
            await using var tx = await db.Database.BeginTransactionAsync();
            await Return(db, files, a, req.FromEmployeeId, req.ItemIds, null, null, $"Devir: {req.Note}".TrimEnd(' ', ':'), key is null ? null : key + ":iade");
            var id = await Issue(db, files, a, req.ToEmployeeId, req.ItemIds, null, req.Note, key is null ? null : key + ":zimmet");
            await tx.CommitAsync();
            return Results.Ok(await Detail(db, a, id));
        });

        api.MapGet("/assignments/{id:guid}/pdf", async (Guid id, HttpContext ctx, AppDbContext db, IFileStore files) =>
        {
            var a = await ctx.Access();
            var doc = await db.Documents.Where(d => d.AssignmentId == id && d.Kind == DocumentKind.Generated && a.Assignments(db).Any(x => x.Id == id))
                .OrderByDescending(d => d.Version).FirstOrDefaultAsync() ?? throw AppException.NotFound("Belge");
            return Results.File(await files.GetAsync(doc.StorageKey), doc.ContentType, doc.FileName);
        });

        api.MapGet("/documents/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, IFileStore files) =>
        {
            var a = await ctx.Access();
            var doc = await db.Documents.Include(d => d.Assignment).FirstOrDefaultAsync(d => d.Id == id) ?? throw AppException.NotFound("Belge");
            if (!a.CanReadWarehouse(doc.Assignment!.WarehouseId)) throw AppException.NotFound("Belge");
            Audit.Add(db, a, "İndirme", "Belge", doc.Id, doc.FileName);
            await db.SaveChangesAsync();
            return Results.File(await files.GetAsync(doc.StorageKey), doc.ContentType, doc.FileName);
        });

        api.MapPost("/assignments/{id:guid}/signed", async (Guid id, HttpContext ctx, AppDbContext db, IFileStore files) =>
        {
            var a = await ctx.Access();
            var asg = await a.Assignments(db).FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("Zimmet");
            a.RequireWriteWarehouse(asg.WarehouseId);
            if (!ctx.Request.HasFormContentType) throw AppException.Invalid("Dosya seçin.");
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault() ?? throw AppException.Invalid("Dosya seçin.");
            if (file.Length == 0) throw AppException.Invalid("Dosya boş.");
            if (file.Length > UploadRules.MaxBytes) throw AppException.Invalid("Dosya 20 MB sınırını aşıyor. Daha düşük çözünürlükte tarayın.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();
            var type = UploadRules.DetectContentType(bytes.AsSpan(0, Math.Min(16, bytes.Length)))
                ?? throw AppException.Invalid("Yalnızca PDF, JPEG veya PNG yüklenebilir. Dosya içeriği bu türlerden biri değil.");
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlAsync($"SELECT id FROM assignments WHERE id = {id} FOR UPDATE");
            var version = (await db.Documents.Where(d => d.AssignmentId == id && d.Kind == DocumentKind.Signed).MaxAsync(d => (int?)d.Version) ?? 0) + 1;
            var safeName = Path.GetFileNameWithoutExtension(file.FileName);
            safeName = new string(safeName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').ToArray()).Trim();
            if (safeName.Length == 0) safeName = "imzali";
            var doc = new Document
            {
                Id = Guid.NewGuid(), AssignmentId = id, Kind = DocumentKind.Signed, Version = version,
                StorageKey = $"evrak/{id}/imzali-v{version}-{Guid.NewGuid():N}{UploadRules.Extension(type)}",
                FileName = $"{asg.No}-imzali-v{version}{UploadRules.Extension(type)}",
                ContentType = type, Size = bytes.Length, Sha256 = sha, At = DateTimeOffset.UtcNow, UserName = a.UserName
            };
            await files.PutAsync(doc.StorageKey, bytes, type);
            db.Documents.Add(doc);
            Audit.Add(db, a, "İmzalı evrak yükleme", "Zimmet", asg.Id, $"{asg.No} sürüm {version} · {safeName} · {sha}");
            var itemIds = await db.AssignmentLines.Where(l => l.AssignmentId == id).Select(l => l.ItemId).ToListAsync();
            foreach (var iid in itemIds) ItemEndpoints.Event(db, iid, "Evrak", $"İmzalı kopya yüklendi (sürüm {version})", a.UserName, asg.No);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new DocumentDto(doc.Id, id, asg.No, doc.Kind, doc.Version, doc.FileName, doc.ContentType, doc.Size, doc.Sha256, doc.At, doc.UserName));
        }).DisableAntiforgery();
    }

    static IQueryable<AssignmentListRow> Project(IQueryable<Assignment> q) => q.Select(x => new AssignmentListRow(
        x.Id, x.No, x.Kind, x.At, x.EmployeeId, x.Employee!.FullName, x.Employee.Department, x.Warehouse!.Name, x.Lines.Count,
        x.Documents.Any(d => d.Kind == DocumentKind.Signed), x.UserName));

    internal static async Task<AssignmentDetail> Detail(AppDbContext db, Access a, Guid id)
    {
        var header = await Project(a.Assignments(db).Where(x => x.Id == id)).FirstOrDefaultAsync() ?? throw AppException.NotFound("Zimmet");
        var asg = await db.Assignments.AsNoTracking().FirstAsync(x => x.Id == id);
        var snap = JsonSerializer.Deserialize<AssignmentSnapshot>(asg.Snapshot)!;
        var lines = await db.AssignmentLines.Where(l => l.AssignmentId == id).Include(l => l.Item).ToListAsync();
        var lineDtos = snap.Lines.Select(s =>
        {
            var l = lines.FirstOrDefault(x => x.Item!.Tag == s.Tag);
            return new AssignmentLineDto(l?.ItemId ?? Guid.Empty, s.Tag, s.Description, s.Serial, s.Extra, s.Condition, l?.IsOpen ?? false);
        }).ToList();
        var docs = await db.Documents.Where(d => d.AssignmentId == id).OrderBy(d => d.Kind).ThenByDescending(d => d.Version)
            .Select(d => new DocumentDto(d.Id, d.AssignmentId, asg.No, d.Kind, d.Version, d.FileName, d.ContentType, d.Size, d.Sha256, d.At, d.UserName)).ToListAsync();
        return new AssignmentDetail(header, asg.Note, lineDtos, docs);
    }

    static async Task<Guid?> ExistingAsync(AppDbContext db, string? key) => string.IsNullOrEmpty(key) ? null :
        await db.Assignments.Where(x => x.IdempotencyKey == key).Select(x => (Guid?)x.Id).FirstOrDefaultAsync();

    /// <summary>Kilitlenen kayıtları sırayla (id) alır; aynı cihaz kümesi için kilitlenme sırası sabit olur.</summary>
    static Task<List<InventoryItem>> LockItems(AppDbContext db, Guid[] ids) =>
        db.Items.FromSql($"SELECT *, xmin FROM items WHERE id = ANY({ids}) ORDER BY id FOR UPDATE").ToListAsync();

    internal static async Task<Guid> Issue(AppDbContext db, IFileStore files, Access a, Guid employeeId, IReadOnlyList<Guid> itemIds,
        IReadOnlyDictionary<Guid, string>? conditions, string? note, string? idempotencyKey)
    {
        a.RequireWrite();
        var ids = (itemIds ?? []).Distinct().ToArray();
        if (ids.Length == 0) throw AppException.Invalid("Zimmetlenecek en az bir cihaz seçin.");
        var emp = await a.Employees(db).Include(e => e.Company).FirstOrDefaultAsync(e => e.Id == employeeId) ?? throw AppException.NotFound("Personel");
        if (!emp.IsActive) throw AppException.Conflict("Pasif personele zimmet yapılamaz.");

        var items = await LockItems(db, ids);
        // Aynı anahtarla eşzamanlı gelen tekrar: kilit alındığında ilk istek tamamlanmış olur, onun sonucu döner.
        if (await ExistingAsync(db, idempotencyKey) is Guid done) return done;
        if (items.Count != ids.Length || items.Any(i => !a.CanReadWarehouse(i.WarehouseId))) throw AppException.NotFound("Seçilen cihazlardan biri");
        var errors = new List<string>();
        foreach (var i in items)
        {
            if (i.Status != ItemStatus.InStock) errors.Add($"{i.Tag} şu an \"{Labels.Status(i.Status)}\" durumunda");
            if (i.CompanyId != emp.CompanyId) errors.Add($"{i.Tag} başka bir şirkete ait; zimmet yalnızca aynı şirketin personeline yapılır");
        }
        if (errors.Count > 0) throw AppException.Conflict("Zimmet yapılamadı: " + string.Join("; ", errors) + ".");
        var warehouseId = items[0].WarehouseId;
        if (items.Any(i => i.WarehouseId != warehouseId)) throw AppException.Conflict("Bir tutanaktaki cihazlar aynı depodan olmalıdır. Farklı depolar için ayrı zimmet yapın.");
        a.RequireWriteWarehouse(warehouseId);

        var now = DateTimeOffset.UtcNow;
        var asg = new Assignment
        {
            Id = Guid.NewGuid(), No = await Counters.DocumentNoAsync(db, "ZMT", now), Kind = AssignmentKind.Issue,
            CompanyId = emp.CompanyId, WarehouseId = warehouseId, EmployeeId = emp.Id, At = now, UserName = a.UserName,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), IdempotencyKey = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey
        };
        foreach (var i in items.OrderBy(i => i.Tag))
        {
            var cond = conditions?.GetValueOrDefault(i.Id);
            asg.Lines.Add(new AssignmentLine { Id = Guid.NewGuid(), ItemId = i.Id, IsOpen = true, Condition = string.IsNullOrWhiteSpace(cond) ? null : cond.Trim() });
            i.Status = ItemStatus.Assigned; i.EmployeeId = emp.Id; i.OpenAssignmentId = asg.Id;
            ItemEndpoints.Event(db, i.Id, "Zimmet", $"{emp.FullName} kişisine zimmetlendi", a.UserName, asg.No);
        }
        db.Assignments.Add(asg);
        await FinishDocument(db, files, a, asg, emp, items);
        Audit.Add(db, a, "Zimmet", "Zimmet", asg.Id, $"{asg.No} · {emp.FullName} · {items.Count} cihaz");
        await db.SaveChangesAsync();
        return asg.Id;
    }

    internal static async Task<Guid> Return(AppDbContext db, IFileStore files, Access a, Guid employeeId, IReadOnlyList<Guid> itemIds, Guid? locationId,
        IReadOnlyDictionary<Guid, string>? conditions, string? note, string? idempotencyKey)
    {
        a.RequireWrite();
        var ids = (itemIds ?? []).Distinct().ToArray();
        if (ids.Length == 0) throw AppException.Invalid("İade alınacak en az bir cihaz seçin.");
        var emp = await a.Employees(db).Include(e => e.Company).FirstOrDefaultAsync(e => e.Id == employeeId) ?? throw AppException.NotFound("Personel");
        var items = await LockItems(db, ids);
        if (await ExistingAsync(db, idempotencyKey) is Guid done) return done;
        if (items.Count != ids.Length || items.Any(i => !a.CanReadWarehouse(i.WarehouseId))) throw AppException.NotFound("Seçilen cihazlardan biri");
        var wrong = items.Where(i => i.Status != ItemStatus.Assigned || i.EmployeeId != emp.Id).Select(i => i.Tag).ToList();
        if (wrong.Count > 0) throw AppException.Conflict($"Şu cihazlar {emp.FullName} kişisine zimmetli değil: {string.Join(", ", wrong)}.");
        var warehouseId = items[0].WarehouseId;
        if (items.Any(i => i.WarehouseId != warehouseId)) throw AppException.Conflict("İade tutanağındaki cihazlar aynı depoya ait olmalıdır.");
        a.RequireWriteWarehouse(warehouseId);
        if (locationId is Guid lid && !await db.Locations.AnyAsync(l => l.Id == lid && l.WarehouseId == warehouseId && !l.Archived))
            throw AppException.Invalid("Lokasyon depoya ait değil.");

        var now = DateTimeOffset.UtcNow;
        var asg = new Assignment
        {
            Id = Guid.NewGuid(), No = await Counters.DocumentNoAsync(db, "IAD", now), Kind = AssignmentKind.Return,
            CompanyId = emp.CompanyId, WarehouseId = warehouseId, EmployeeId = emp.Id, At = now, UserName = a.UserName,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), IdempotencyKey = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey
        };
        var openLines = await db.AssignmentLines.Where(l => ids.Contains(l.ItemId) && l.IsOpen).ToListAsync();
        foreach (var l in openLines) { l.IsOpen = false; l.ClosedByAssignmentId = asg.Id; }
        foreach (var i in items.OrderBy(i => i.Tag))
        {
            var cond = conditions?.GetValueOrDefault(i.Id);
            asg.Lines.Add(new AssignmentLine { Id = Guid.NewGuid(), ItemId = i.Id, IsOpen = false, Condition = string.IsNullOrWhiteSpace(cond) ? null : cond.Trim() });
            i.Status = ItemStatus.InStock; i.EmployeeId = null; i.OpenAssignmentId = null;
            if (locationId is not null) i.LocationId = locationId;
            ItemEndpoints.Event(db, i.Id, "İade", $"{emp.FullName} kişisinden iade alındı", a.UserName, asg.No);
        }
        db.Assignments.Add(asg);
        // Kapanan satırların indeks güncellemesi yeni açılacak satırlardan önce veritabanına yazılsın.
        await db.SaveChangesAsync();
        await FinishDocument(db, files, a, asg, emp, items);
        Audit.Add(db, a, "İade", "Zimmet", asg.Id, $"{asg.No} · {emp.FullName} · {items.Count} cihaz");
        await db.SaveChangesAsync();
        return asg.Id;
    }

    /// <summary>İşlem anındaki verilerden dondurulmuş belge üretir ve özel depoya yazar.</summary>
    static async Task FinishDocument(AppDbContext db, IFileStore files, Access a, Assignment asg, Employee emp, List<InventoryItem> items)
    {
        var ids = items.Select(i => i.Id).ToList();
        var models = await db.Items.Where(i => ids.Contains(i.Id))
            .Select(i => new { i.Id, Brand = i.AssetModel!.Manufacturer!.Name, Model = i.AssetModel.Name }).ToListAsync();
        var values = await db.FieldValues.Where(v => ids.Contains(v.ItemId) && !v.Field!.Archived)
            .Select(v => new { v.ItemId, v.Field!.Label, v.Field.Type, v.Field.SortOrder, v.Text }).ToListAsync();
        var wh = await db.Warehouses.Where(w => w.Id == asg.WarehouseId).Select(w => w.Name).FirstAsync();
        var sims = await db.PhoneLines.Where(l => l.ItemId != null && ids.Contains(l.ItemId.Value) && l.Status != LineStatus.Cancelled)
            .Select(l => new { ItemId = l.ItemId!.Value, l.Number, l.Operator }).ToListAsync();

        var snap = new AssignmentSnapshot(asg.No, asg.Kind, TimeZones.Format(asg.At), emp.Company!.Name, wh, emp.FullName,
            emp.RegistryNo, emp.Title, emp.Department, string.IsNullOrEmpty(a.DisplayName) ? a.UserName : a.DisplayName, asg.Note,
            items.OrderBy(i => i.Tag).Select(i =>
            {
                var m = models.First(x => x.Id == i.Id);
                var extra = string.Join("\n", values.Where(v => v.ItemId == i.Id).OrderBy(v => v.SortOrder)
                    .Select(v => $"{v.Label}: {FieldRules.Display(v.Type, v.Text)}")
                    .Concat(sims.Where(x => x.ItemId == i.Id).OrderBy(x => x.Number).Select(x => $"Hat: {LineLabels.Number(x.Number)} ({x.Operator})")));
                return new SnapshotLine(i.Tag, $"{m.Brand} {m.Model}", i.Serial, extra.Length == 0 ? null : extra,
                    asg.Lines.First(l => l.ItemId == i.Id).Condition);
            }).ToList());
        asg.Snapshot = JsonSerializer.Serialize(snap);

        var pdf = AssignmentPdf.Render(snap);
        var doc = new Document
        {
            Id = Guid.NewGuid(), AssignmentId = asg.Id, Kind = DocumentKind.Generated, Version = 1,
            StorageKey = $"evrak/{asg.Id}/tutanak-v1-{Guid.NewGuid():N}.pdf", FileName = $"{asg.No}.pdf",
            ContentType = "application/pdf", Size = pdf.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(pdf)),
            At = asg.At, UserName = a.UserName
        };
        await files.PutAsync(doc.StorageKey, pdf, doc.ContentType);
        db.Documents.Add(doc);
    }
}

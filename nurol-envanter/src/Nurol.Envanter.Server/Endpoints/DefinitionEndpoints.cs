using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Server.Services;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

public static class DefinitionEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // ---- Şirketler ----
        api.MapGet("/companies", async (HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            return await a.CompaniesQuery(db).OrderBy(c => c.Name).Select(c => new CompanyDto(c.Id, c.Code, c.Name, c.Archived)).ToListAsync();
        });
        api.MapPost("/companies", async (CompanySave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireSystemAdmin();
            var c = new Company { Id = Guid.NewGuid() };
            Apply(c, req);
            db.Companies.Add(c);
            Audit.Add(db, a, "Oluşturma", "Şirket", c.Id, c.Name);
            await db.SaveChangesAsync();
            return Results.Ok(new CompanyDto(c.Id, c.Code, c.Name, c.Archived));
        });
        api.MapPut("/companies/{id:guid}", async (Guid id, CompanySave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireSystemAdmin();
            var c = await db.Companies.FindAsync(id) ?? throw AppException.NotFound("Şirket");
            if (c.Code != req.Code.Trim().ToUpperInvariant() && await db.Items.AnyAsync(i => i.CompanyId == id))
                throw AppException.Conflict("Envanter etiketlerinde kullanıldığı için şirket kodu değiştirilemez.");
            Apply(c, req);
            Audit.Add(db, a, "Güncelleme", "Şirket", c.Id, c.Name);
            await db.SaveChangesAsync();
            return Results.Ok(new CompanyDto(c.Id, c.Code, c.Name, c.Archived));
        });

        // ---- Depolar ve lokasyonlar ----
        api.MapGet("/warehouses", async (HttpContext ctx, AppDbContext db, bool? includeArchived) =>
        {
            var a = await ctx.Access();
            var q = a.WarehousesQuery(db);
            if (includeArchived != true) q = q.Where(w => !w.Archived);
            var list = await q.OrderBy(w => w.Company!.Name).ThenBy(w => w.Name)
                .Select(w => new WarehouseDto(w.Id, w.CompanyId, w.Company!.Name, w.Name, w.Address, w.Archived,
                    w.Locations.Where(l => includeArchived == true || !l.Archived).OrderBy(l => l.Name).Select(l => new LocationDto(l.Id, l.WarehouseId, l.Name, l.Archived)).ToList(),
                    db.Items.Count(i => i.WarehouseId == w.Id && i.Status != ItemStatus.Retired)))
                .ToListAsync();
            return list;
        });
        // Transfer hedefi seçimi: kullanıcının şirketlerindeki tüm aktif depolar (yalnızca ad; envanter içeriği göstermez).
        api.MapGet("/warehouses/targets", async (HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var q = db.Warehouses.Where(w => !w.Archived);
            if (!a.IsSystemAdmin) q = q.Where(w => a.Companies.Contains(w.CompanyId));
            return await q.OrderBy(w => w.Name)
                .Select(w => new WarehouseDto(w.Id, w.CompanyId, w.Company!.Name, w.Name, null, false, new List<LocationDto>(), 0)).ToListAsync();
        });
        api.MapPost("/warehouses", async (WarehouseSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            if (!a.CanAdministerCompany(req.CompanyId)) throw AppException.Forbidden("Bu şirkete depo ekleme yetkiniz yok.");
            Require(req.Name, "Depo adı");
            var w = new Warehouse { Id = Guid.NewGuid(), CompanyId = req.CompanyId, Name = req.Name.Trim(), Address = req.Address?.Trim() };
            db.Warehouses.Add(w);
            Audit.Add(db, a, "Oluşturma", "Depo", w.Id, w.Name);
            await db.SaveChangesAsync();
            return Results.Ok(new { w.Id });
        });
        api.MapPut("/warehouses/{id:guid}", async (Guid id, WarehouseSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var w = await db.Warehouses.FindAsync(id) ?? throw AppException.NotFound("Depo");
            if (!a.CanAdministerCompany(w.CompanyId)) throw AppException.Forbidden();
            if (req.CompanyId != w.CompanyId) throw AppException.Conflict("Depo başka şirkete taşınamaz.");
            Require(req.Name, "Depo adı");
            w.Name = req.Name.Trim(); w.Address = req.Address?.Trim();
            Audit.Add(db, a, "Güncelleme", "Depo", w.Id, w.Name);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
        api.MapPost("/warehouses/{id:guid}/archive", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var w = await db.Warehouses.FindAsync(id) ?? throw AppException.NotFound("Depo");
            if (!a.CanAdministerCompany(w.CompanyId)) throw AppException.Forbidden();
            if (await db.Items.AnyAsync(i => i.WarehouseId == id && i.Status != ItemStatus.Retired) ||
                await db.StockBalances.AnyAsync(b => b.WarehouseId == id && b.Quantity > 0))
                throw AppException.Conflict("Depoda aktif envanter veya stok bakiyesi varken arşivlenemez.");
            w.Archived = !w.Archived;
            Audit.Add(db, a, w.Archived ? "Arşivleme" : "Arşivden çıkarma", "Depo", w.Id, w.Name);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
        api.MapPost("/locations", async (LocationSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var w = await db.Warehouses.FindAsync(req.WarehouseId) ?? throw AppException.NotFound("Depo");
            if (!a.CanAdministerCompany(w.CompanyId) && !a.CanWriteWarehouse(w.Id)) throw AppException.Forbidden();
            Require(req.Name, "Lokasyon adı");
            var l = new Location { Id = Guid.NewGuid(), WarehouseId = w.Id, Name = req.Name.Trim() };
            db.Locations.Add(l);
            await db.SaveChangesAsync();
            return Results.Ok(new LocationDto(l.Id, l.WarehouseId, l.Name, false));
        });
        api.MapPost("/locations/{id:guid}/archive", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var l = await db.Locations.Include(x => x.Warehouse).FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("Lokasyon");
            if (!a.CanAdministerCompany(l.Warehouse!.CompanyId) && !a.CanWriteWarehouse(l.WarehouseId)) throw AppException.Forbidden();
            if (!l.Archived && await db.Items.AnyAsync(i => i.LocationId == id && i.Status != ItemStatus.Retired))
                throw AppException.Conflict("Bu lokasyonda kayıtlı cihazlar var. Önce cihazların lokasyonunu değiştirin.");
            l.Archived = !l.Archived;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- Kategoriler ----
        api.MapGet("/categories", async (AppDbContext db, bool? includeArchived) =>
            await db.Categories.Where(c => includeArchived == true || !c.Archived).OrderBy(c => c.Name)
                .Select(c => new CategoryDto(c.Id, c.Code, c.Name, c.RequiresSerial, c.Archived, db.FieldDefinitions.Count(f => f.CategoryId == c.Id && !f.Archived)))
                .ToListAsync());
        api.MapPost("/categories", async (CategorySave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireDefinitions();
            var c = new Category { Id = Guid.NewGuid() };
            Apply(c, req);
            db.Categories.Add(c);
            Audit.Add(db, a, "Oluşturma", "Kategori", c.Id, c.Name);
            await db.SaveChangesAsync();
            return Results.Ok(new { c.Id });
        });
        api.MapPut("/categories/{id:guid}", async (Guid id, CategorySave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireDefinitions();
            var c = await db.Categories.FindAsync(id) ?? throw AppException.NotFound("Kategori");
            var used = await db.Items.AnyAsync(i => i.CategoryId == id);
            if (used && c.Code != req.Code.Trim().ToUpperInvariant())
                throw AppException.Conflict("Envanter etiketlerinde kullanıldığı için kategori kodu değiştirilemez.");
            if (req.RequiresSerial && !c.RequiresSerial)
            {
                var missing = await db.Items.Where(i => i.CategoryId == id && i.Serial == null).Select(i => i.Tag).Take(50).ToListAsync();
                if (missing.Count > 0)
                    throw AppException.Conflict($"Seri numarası olmayan {missing.Count} kayıt var: {string.Join(", ", missing.Take(10))}. Önce bu kayıtları tamamlayın.");
            }
            Apply(c, req);
            Audit.Add(db, a, "Güncelleme", "Kategori", c.Id, c.Name);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
        api.MapPost("/categories/{id:guid}/archive", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireDefinitions();
            var c = await db.Categories.FindAsync(id) ?? throw AppException.NotFound("Kategori");
            c.Archived = !c.Archived;
            Audit.Add(db, a, c.Archived ? "Arşivleme" : "Arşivden çıkarma", "Kategori", c.Id, c.Name);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- Üreticiler ve modeller ----
        api.MapGet("/manufacturers", async (AppDbContext db) =>
            await db.Manufacturers.OrderBy(m => m.Name).Select(m => new ManufacturerDto(m.Id, m.Name)).ToListAsync());
        api.MapPost("/manufacturers", async (ManufacturerSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireDefinitions();
            Require(req.Name, "Marka adı");
            var m = new Manufacturer { Id = Guid.NewGuid(), Name = req.Name.Trim() };
            db.Manufacturers.Add(m);
            await db.SaveChangesAsync();
            return Results.Ok(new ManufacturerDto(m.Id, m.Name));
        });
        api.MapGet("/models", async (AppDbContext db, Guid? categoryId, bool? includeArchived) =>
            await db.AssetModels.Where(m => (categoryId == null || m.CategoryId == categoryId) && (includeArchived == true || !m.Archived))
                .OrderBy(m => m.Manufacturer!.Name).ThenBy(m => m.Name)
                .Select(m => new AssetModelDto(m.Id, m.CategoryId, m.Category!.Name, m.ManufacturerId, m.Manufacturer!.Name, m.Name, m.Archived)).ToListAsync());
        api.MapPost("/models", async (AssetModelSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireDefinitions();
            Require(req.Name, "Model adı");
            var m = new AssetModel { Id = Guid.NewGuid(), CategoryId = req.CategoryId, ManufacturerId = req.ManufacturerId, Name = req.Name.Trim() };
            db.AssetModels.Add(m);
            Audit.Add(db, a, "Oluşturma", "Model", m.Id, m.Name);
            await db.SaveChangesAsync();
            return Results.Ok(new { m.Id });
        });
        api.MapPut("/models/{id:guid}", async (Guid id, AssetModelSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireDefinitions();
            var m = await db.AssetModels.FindAsync(id) ?? throw AppException.NotFound("Model");
            if ((m.CategoryId != req.CategoryId || m.ManufacturerId != req.ManufacturerId) && await db.Items.AnyAsync(i => i.AssetModelId == id))
                throw AppException.Conflict("Kullanımdaki modelin kategorisi veya markası değiştirilemez.");
            Require(req.Name, "Model adı");
            m.Name = req.Name.Trim(); m.CategoryId = req.CategoryId; m.ManufacturerId = req.ManufacturerId;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
        api.MapPost("/models/{id:guid}/archive", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireDefinitions();
            var m = await db.AssetModels.FindAsync(id) ?? throw AppException.NotFound("Model");
            m.Archived = !m.Archived;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- Özel alanlar ----
        api.MapGet("/fields", async (AppDbContext db, Guid? categoryId, Guid? modelId, bool? includeArchived) =>
        {
            var q = db.FieldDefinitions.AsQueryable();
            if (categoryId != null && modelId != null) q = q.Where(f => f.CategoryId == categoryId || f.AssetModelId == modelId);
            else if (categoryId != null) q = q.Where(f => f.CategoryId == categoryId);
            else if (modelId != null) q = q.Where(f => f.AssetModelId == modelId);
            if (includeArchived != true) q = q.Where(f => !f.Archived);
            return await q.OrderBy(f => f.AssetModelId != null).ThenBy(f => f.SortOrder).ThenBy(f => f.Label)
                .Select(f => ToDto(f, db.FieldValues.Count(v => v.FieldId == f.Id))).ToListAsync();
        });
        api.MapGet("/fields/all", async (AppDbContext db) =>
            await db.FieldDefinitions.OrderBy(f => f.Label).Select(f => ToDto(f, 0)).ToListAsync());
        api.MapPost("/fields", async (FieldDefinitionSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireDefinitions();
            Validate(req);
            if ((req.CategoryId == null) == (req.AssetModelId == null))
                throw AppException.Invalid("Alan bir kategoriye veya bir modele bağlı olmalıdır.");
            await EnsureNoDuplicateLabel(db, req, null);
            var key = FieldRules.Slug(req.Label);
            var baseKey = key; var n = 2;
            while (await db.FieldDefinitions.AnyAsync(f => f.Key == key)) key = $"{baseKey}_{n++}";
            var f = new FieldDefinition { Id = Guid.NewGuid(), Key = key, CategoryId = req.CategoryId, AssetModelId = req.AssetModelId, Type = req.Type };
            ApplyField(f, req);
            // Yeni zorunlu alan mevcut kayıtlarla çelişiyorsa sessizce eklenmez.
            if (f.Required)
            {
                var exists = await db.Items.AnyAsync(i => req.CategoryId != null ? i.CategoryId == req.CategoryId : i.AssetModelId == req.AssetModelId);
                if (exists)
                    throw AppException.Conflict("Bu kapsamda mevcut kayıtlar var. Alanı önce zorunlu olmadan ekleyin, değerleri tamamlayınca zorunlu yapın.");
            }
            db.FieldDefinitions.Add(f);
            Audit.Add(db, a, "Oluşturma", "Özel alan", f.Id, f.Label);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(f, 0));
        });
        api.MapPost("/fields/{id:guid}/check", async (Guid id, FieldDefinitionSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireDefinitions();
            var f = await db.FieldDefinitions.FindAsync(id) ?? throw AppException.NotFound("Alan");
            return await FieldRules.ConflictsAsync(db, f, req);
        });
        api.MapPut("/fields/{id:guid}", async (Guid id, FieldDefinitionSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireDefinitions();
            Validate(req);
            var f = await db.FieldDefinitions.FindAsync(id) ?? throw AppException.NotFound("Alan");
            if (f.Archived) throw AppException.Conflict("Arşivlenmiş alan düzenlenemez. Önce arşivden çıkarın.");
            var count = await db.FieldValues.CountAsync(v => v.FieldId == id);
            if (req.Type != f.Type && count > 0) throw AppException.Conflict("Değer girilmiş alanın tipi değiştirilemez.");
            await EnsureNoDuplicateLabel(db, req with { CategoryId = f.CategoryId, AssetModelId = f.AssetModelId }, id);
            var conflicts = await FieldRules.ConflictsAsync(db, f, req);
            if (conflicts.Count > 0)
                return Results.Json(new { message = "Yeni kural mevcut kayıtlarla çelişiyor. Önce aşağıdaki kayıtları düzeltin.", conflicts }, statusCode: 409);

            var wasUnique = f.Unique;
            ApplyField(f, req);
            f.Type = req.Type;
            if (f.Unique && !wasUnique)
            {
                var vals = await db.FieldValues.Where(v => v.FieldId == id && v.Text != null).Select(v => new { v.ItemId, v.Text }).ToListAsync();
                foreach (var v in vals) db.UniqueFieldValues.Add(new UniqueFieldValue { FieldId = id, ItemId = v.ItemId, Value = FieldRules.UniqueKey(v.Text!) });
            }
            else if (!f.Unique && wasUnique)
                await db.UniqueFieldValues.Where(u => u.FieldId == id).ExecuteDeleteAsync();
            Audit.Add(db, a, "Güncelleme", "Özel alan", f.Id, f.Label);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(f, count));
        });
        api.MapDelete("/fields/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireDefinitions();
            var f = await db.FieldDefinitions.FindAsync(id) ?? throw AppException.NotFound("Alan");
            var used = await db.FieldValues.AnyAsync(v => v.FieldId == id);
            if (used)
            {
                // Kullanımdaki alan silinmez, arşivlenir; eski değerler ve geçmiş korunur.
                f.Archived = true;
                await db.UniqueFieldValues.Where(u => u.FieldId == id).ExecuteDeleteAsync();
                Audit.Add(db, a, "Arşivleme", "Özel alan", f.Id, f.Label);
                await db.SaveChangesAsync();
                return Results.Ok(new { archived = true });
            }
            db.FieldDefinitions.Remove(f);
            Audit.Add(db, a, "Silme", "Özel alan", f.Id, f.Label);
            await db.SaveChangesAsync();
            return Results.Ok(new { archived = false });
        });
        api.MapPost("/fields/{id:guid}/restore", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireDefinitions();
            var f = await db.FieldDefinitions.FindAsync(id) ?? throw AppException.NotFound("Alan");
            if (f.Unique)
            {
                var vals = await db.FieldValues.Where(v => v.FieldId == id && v.Text != null).Select(v => new { v.ItemId, v.Text }).ToListAsync();
                var dup = vals.GroupBy(v => FieldRules.UniqueKey(v.Text!)).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (dup.Count > 0) throw AppException.Conflict($"Arşivdeyken tekrar eden değerler oluşmuş ({string.Join(", ", dup.Take(5))}). Benzersizlik kuralı kapatılarak geri alınabilir.");
                foreach (var v in vals) db.UniqueFieldValues.Add(new UniqueFieldValue { FieldId = id, ItemId = v.ItemId, Value = FieldRules.UniqueKey(v.Text!) });
            }
            f.Archived = false;
            if (f.Required) f.Required = false; // Arşivdeyken eklenen kayıtlar boş olabilir; zorunluluk yönetici tarafından yeniden açılır.
            Audit.Add(db, a, "Arşivden çıkarma", "Özel alan", f.Id, f.Label);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    static FieldDefinitionDto ToDto(FieldDefinition f, int count) => new(
        f.Id, f.Key, f.Label, f.HelpText, f.Type, f.CategoryId, f.AssetModelId, f.SortOrder, f.Required, f.Unique,
        f.MaxLength, f.DigitsOnly, f.ExactLength, f.Options, f.Archived, count);

    static void ApplyField(FieldDefinition f, FieldDefinitionSave r)
    {
        f.Label = r.Label.Trim();
        f.HelpText = string.IsNullOrWhiteSpace(r.HelpText) ? null : r.HelpText.Trim();
        f.SortOrder = r.SortOrder;
        f.Required = r.Required;
        f.Unique = r.Unique && r.Type is FieldType.Text or FieldType.Number;
        f.MaxLength = r.Type == FieldType.Text ? r.MaxLength : null;
        f.DigitsOnly = r.Type == FieldType.Text && r.DigitsOnly;
        f.ExactLength = r.Type == FieldType.Text ? r.ExactLength : null;
        f.Options = r.Type == FieldType.Select ? (r.Options ?? []).Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList() : [];
    }

    static void Validate(FieldDefinitionSave r)
    {
        Require(r.Label, "Alan adı");
        if (r.Type == FieldType.Select && (r.Options is null || !r.Options.Any(o => !string.IsNullOrWhiteSpace(o))))
            throw AppException.Invalid("Seçim listesi için en az bir seçenek girin.");
        if (r.MaxLength is < 1 or > 2000) throw AppException.Invalid("Uzunluk sınırı 1 ile 2000 arasında olmalıdır.");
        if (r.ExactLength is < 1 or > 2000) throw AppException.Invalid("Hane sayısı 1 ile 2000 arasında olmalıdır.");
    }

    static async Task EnsureNoDuplicateLabel(AppDbContext db, FieldDefinitionSave r, Guid? self)
    {
        // Kategori varsayılanı ile model ek alanlarının birleşiminde aynı alan iki kez görünmesin.
        var label = r.Label.Trim().ToLower();
        Guid? categoryId = r.CategoryId;
        List<Guid> modelIds = [];
        if (r.AssetModelId is Guid mid)
        {
            categoryId = await db.AssetModels.Where(m => m.Id == mid).Select(m => (Guid?)m.CategoryId).FirstOrDefaultAsync();
            modelIds.Add(mid);
        }
        else if (categoryId is Guid cid)
            modelIds = await db.AssetModels.Where(m => m.CategoryId == cid).Select(m => m.Id).ToListAsync();

        var clash = await db.FieldDefinitions.AnyAsync(f => f.Id != self && !f.Archived && f.Label.ToLower() == label &&
            (f.CategoryId == categoryId || (f.AssetModelId != null && modelIds.Contains(f.AssetModelId.Value))));
        if (clash) throw AppException.Conflict($"\"{r.Label.Trim()}\" adlı alan bu kategori veya modelinde zaten var.");
    }

    static void Apply(Company c, CompanySave r)
    {
        Require(r.Name, "Şirket adı");
        var code = (r.Code ?? "").Trim().ToUpperInvariant();
        if (code.Length is < 2 or > 6 || !code.All(char.IsAsciiLetterOrDigit))
            throw AppException.Invalid("Şirket kodu 2-6 harf veya rakam olmalıdır (ör. NRL).");
        c.Code = code; c.Name = r.Name.Trim();
    }

    static void Apply(Category c, CategorySave r)
    {
        Require(r.Name, "Kategori adı");
        var code = (r.Code ?? "").Trim().ToUpperInvariant();
        if (code.Length is < 2 or > 6 || !code.All(char.IsAsciiLetterOrDigit))
            throw AppException.Invalid("Kategori kodu 2-6 harf veya rakam olmalıdır (ör. TLF).");
        c.Code = code; c.Name = r.Name.Trim(); c.RequiresSerial = r.RequiresSerial;
    }

    internal static void Require(string? v, string label)
    {
        if (string.IsNullOrWhiteSpace(v)) throw AppException.Invalid($"{label} zorunludur.");
    }
}

public static class Audit
{
    public static void Add(AppDbContext db, Access a, string action, string entity, object? id, string? details) =>
        db.AuditEvents.Add(new AuditEvent { At = DateTimeOffset.UtcNow, UserName = a.UserName, Action = action, Entity = entity, EntityId = id?.ToString(), Details = details });
}

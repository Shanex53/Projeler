using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

public static class StockEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/stock", async (HttpContext ctx, AppDbContext db, Guid? companyId, string? q, bool? includeArchived) =>
        {
            var a = await ctx.Access();
            var items = a.StockItems(db).AsNoTracking();
            if (companyId is Guid c) items = items.Where(s => s.CompanyId == c);
            if (includeArchived != true) items = items.Where(s => !s.Archived);
            if (!string.IsNullOrWhiteSpace(q)) items = items.Where(s => EF.Functions.ILike(s.Name, $"%{q.Trim()}%") || EF.Functions.ILike(s.Code, $"%{q.Trim()}%"));
            var list = await items.OrderBy(s => s.Name).ToListAsync();
            var whs = await a.WarehousesQuery(db).Where(w => !w.Archived).OrderBy(w => w.Name).Select(w => new { w.Id, w.Name, w.CompanyId }).ToListAsync();
            var ids = list.Select(s => s.Id).ToList();
            var bals = await db.StockBalances.Where(b => ids.Contains(b.StockItemId)).ToListAsync();
            return list.Select(s =>
            {
                var rows = whs.Where(w => w.CompanyId == s.CompanyId)
                    .Select(w => new StockBalanceDto(w.Id, w.Name, bals.FirstOrDefault(b => b.StockItemId == s.Id && b.WarehouseId == w.Id)?.Quantity ?? 0)).ToList();
                return new StockItemDto(s.Id, s.CompanyId, s.Code, s.Name, s.Unit, s.MinLevel, s.Archived, rows, rows.Sum(r => r.Quantity));
            }).ToList();
        });

        api.MapPost("/stock", async (StockItemSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            if (!a.CanWriteCompany(req.CompanyId)) throw AppException.Forbidden("Bu şirkete malzeme ekleme yetkiniz yok.");
            var s = new StockItem { Id = Guid.NewGuid(), CompanyId = req.CompanyId };
            Apply(s, req);
            db.StockItems.Add(s);
            Audit.Add(db, a, "Oluşturma", "Malzeme", s.Id, s.Name);
            await db.SaveChangesAsync();
            return Results.Ok(new { s.Id });
        });

        api.MapPut("/stock/{id:guid}", async (Guid id, StockItemSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var s = await a.StockItems(db).FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("Malzeme");
            if (!a.CanWriteCompany(s.CompanyId)) throw AppException.Forbidden();
            if (req.CompanyId != s.CompanyId) throw AppException.Conflict("Malzeme başka şirkete taşınamaz.");
            if (req.Unit.Trim() != s.Unit && await db.StockMovements.AnyAsync(m => m.StockItemId == id))
                throw AppException.Conflict("Hareket görmüş malzemenin birimi değiştirilemez.");
            Apply(s, req);
            Audit.Add(db, a, "Güncelleme", "Malzeme", s.Id, s.Name);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        api.MapPost("/stock/{id:guid}/archive", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var s = await a.StockItems(db).FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("Malzeme");
            if (!a.CanWriteCompany(s.CompanyId)) throw AppException.Forbidden();
            if (!s.Archived && await db.StockBalances.AnyAsync(b => b.StockItemId == id && b.Quantity > 0))
                throw AppException.Conflict("Bakiyesi olan malzeme arşivlenemez.");
            s.Archived = !s.Archived;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        api.MapGet("/stock/movements", async (HttpContext ctx, AppDbContext db, Guid? stockItemId, Guid? warehouseId, Guid? employeeId, int? page, int? pageSize) =>
        {
            var a = await ctx.Access();
            var q = a.StockMovements(db).AsNoTracking();
            if (stockItemId is Guid s) q = q.Where(m => m.StockItemId == s);
            if (warehouseId is Guid w) q = q.Where(m => m.WarehouseId == w);
            if (employeeId is Guid e) q = q.Where(m => m.EmployeeId == e);
            var p = Math.Max(1, page ?? 1); var size = Math.Clamp(pageSize ?? 50, 1, 500);
            var total = await q.CountAsync();
            var rows = await Project(q.OrderByDescending(m => m.At).Skip((p - 1) * size).Take(size)).ToListAsync();
            return new PagedResult<StockMovementDto>(rows, total, p, size);
        });

        api.MapPost("/stock/movements", async (StockMovementRequest req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var key = ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
            var result = await Move(db, a, req, key);
            return Results.Ok(result);
        });
    }

    internal static IQueryable<StockMovementDto> Project(IQueryable<StockMovement> q) => q.Select(m => new StockMovementDto(
        m.Id, m.No, m.At, m.Type, m.StockItem!.Name, m.StockItem.Unit, m.Warehouse!.Name, m.Quantity, m.BalanceAfter,
        m.Employee != null ? m.Employee.FullName : null, m.Project, m.Note, m.UserName));

    /// <summary>
    /// Stok hareketi. Çıkışlar koşullu UPDATE ile yapılır: bakiye yetersizse hiçbir satır değişmez,
    /// eşzamanlı iki çıkış bakiyeyi eksiye düşüremez. Veritabanı CHECK kısıtı son güvencedir.
    /// </summary>
    internal static async Task<StockMovementDto> Move(AppDbContext db, Access a, StockMovementRequest req, string? idempotencyKey, CancellationToken ct = default)
    {
        a.RequireWriteWarehouse(req.WarehouseId);
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var prev = await Project(db.StockMovements.Where(m => m.IdempotencyKey == idempotencyKey)).FirstOrDefaultAsync(ct);
            if (prev is not null) return prev;
        }
        var item = await db.StockItems.FirstOrDefaultAsync(s => s.Id == req.StockItemId && !s.Archived, ct) ?? throw AppException.NotFound("Malzeme");
        var wh = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == req.WarehouseId && !w.Archived, ct) ?? throw AppException.NotFound("Depo");
        if (wh.CompanyId != item.CompanyId) throw AppException.Conflict("Malzeme ve depo aynı şirkete ait olmalıdır.");
        if (req.Quantity == 0 || (req.Type != StockMovementType.Adjust && req.Quantity < 0))
            throw AppException.Invalid("Miktar sıfırdan büyük olmalıdır.", new() { ["quantity"] = "Geçerli bir miktar girin." });
        Employee? emp = null;
        if (req.Type == StockMovementType.Out)
        {
            if (req.EmployeeId is null && string.IsNullOrWhiteSpace(req.Project))
                throw AppException.Invalid("Sarf çıkışında teslim alan personel veya proje belirtilmelidir.", new() { ["employee"] = "Personel veya proje seçin." });
            if (req.EmployeeId is Guid eid)
            {
                emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == eid, ct) ?? throw AppException.NotFound("Personel");
                if (emp.CompanyId != item.CompanyId) throw AppException.Conflict("Sarf çıkışı yalnızca aynı şirketin personeline yapılabilir.");
                if (!emp.IsActive) throw AppException.Conflict("Pasif personele çıkış yapılamaz.");
            }
        }
        if (req.Type == StockMovementType.Adjust && string.IsNullOrWhiteSpace(req.Note))
            throw AppException.Invalid("Düzeltme hareketinde açıklama zorunludur.", new() { ["note"] = "Düzeltmenin nedenini yazın." });

        var signed = req.Type switch
        {
            StockMovementType.In => req.Quantity,
            StockMovementType.Out => -req.Quantity,
            _ => req.Quantity
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        decimal after;
        if (signed > 0)
        {
            after = (await db.Database.SqlQuery<decimal>($"""
                INSERT INTO stock_balances (stock_item_id, warehouse_id, quantity) VALUES ({item.Id}, {wh.Id}, {signed})
                ON CONFLICT (stock_item_id, warehouse_id) DO UPDATE SET quantity = stock_balances.quantity + EXCLUDED.quantity
                RETURNING quantity AS "Value"
                """).ToListAsync(ct))[0];
        }
        else
        {
            var take = -signed;
            var rows = await db.Database.SqlQuery<decimal>($"""
                UPDATE stock_balances SET quantity = quantity - {take}
                WHERE stock_item_id = {item.Id} AND warehouse_id = {wh.Id} AND quantity >= {take}
                RETURNING quantity AS "Value"
                """).ToListAsync(ct);
            if (rows.Count == 0)
            {
                var have = await db.StockBalances.Where(b => b.StockItemId == item.Id && b.WarehouseId == wh.Id).Select(b => (decimal?)b.Quantity).FirstOrDefaultAsync(ct) ?? 0;
                var tr = CultureInfo.GetCultureInfo("tr-TR");
                throw AppException.Conflict($"{wh.Name} deposunda {have.ToString("#,##0.###", tr)} {item.Unit} {item.Name} var. En fazla bu kadar çıkış yapabilirsiniz.");
            }
            after = rows[0];
        }

        var m = new StockMovement
        {
            Id = Guid.NewGuid(),
            No = await Counters.DocumentNoAsync(db, "STK", DateTimeOffset.UtcNow, ct),
            At = DateTimeOffset.UtcNow, Type = req.Type, StockItemId = item.Id, WarehouseId = wh.Id,
            Quantity = signed, BalanceAfter = after, EmployeeId = emp?.Id,
            Project = string.IsNullOrWhiteSpace(req.Project) ? null : req.Project.Trim(),
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            UserName = a.UserName, IdempotencyKey = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey
        };
        db.StockMovements.Add(m);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (Idempotency.IsKeyViolation(ex))
        {
            // Aynı anahtarla eşzamanlı gelen tekrar isteği: bu işlem geri alınır, ilk kaydın sonucu döner.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await Project(db.StockMovements.Where(x => x.IdempotencyKey == idempotencyKey)).FirstAsync(ct);
        }
        return new StockMovementDto(m.Id, m.No, m.At, m.Type, item.Name, item.Unit, wh.Name, m.Quantity, m.BalanceAfter, emp?.FullName, m.Project, m.Note, m.UserName);
    }

    /// <summary>Bakiyeye ekler (yoksa oluşturur) ve yeni bakiyeyi döner.</summary>
    internal static async Task<decimal> AddBalance(AppDbContext db, Guid stockItemId, Guid warehouseId, decimal qty, CancellationToken ct = default) =>
        (await db.Database.SqlQuery<decimal>($"""
            INSERT INTO stock_balances (stock_item_id, warehouse_id, quantity) VALUES ({stockItemId}, {warehouseId}, {qty})
            ON CONFLICT (stock_item_id, warehouse_id) DO UPDATE SET quantity = stock_balances.quantity + EXCLUDED.quantity
            RETURNING quantity AS "Value"
            """).ToListAsync(ct))[0];

    /// <summary>Bakiye yetiyorsa düşer ve yeni bakiyeyi döner; yetmiyorsa hiçbir şey değişmez ve null döner.</summary>
    internal static async Task<decimal?> TryTakeBalance(AppDbContext db, Guid stockItemId, Guid warehouseId, decimal qty, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<decimal>($"""
            UPDATE stock_balances SET quantity = quantity - {qty}
            WHERE stock_item_id = {stockItemId} AND warehouse_id = {warehouseId} AND quantity >= {qty}
            RETURNING quantity AS "Value"
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }

    static void Apply(StockItem s, StockItemSave r)
    {
        DefinitionEndpoints.Require(r.Name, "Malzeme adı");
        DefinitionEndpoints.Require(r.Code, "Malzeme kodu");
        DefinitionEndpoints.Require(r.Unit, "Birim");
        if (r.MinLevel < 0) throw AppException.Invalid("Kritik seviye negatif olamaz.");
        s.Code = r.Code.Trim().ToUpperInvariant(); s.Name = r.Name.Trim(); s.Unit = r.Unit.Trim(); s.MinLevel = r.MinLevel;
    }
}

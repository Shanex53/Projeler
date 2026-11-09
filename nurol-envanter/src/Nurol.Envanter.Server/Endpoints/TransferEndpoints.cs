using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

/// <summary>
/// Depolar arası transfer: taslak → gönderildi → teslim alındı. Gönderme kaynak, teslim alma hedef depo yetkisi ister.
/// Gönderilen cihaz "transferde" olur ve başka işleme konu olamaz; miktarlı stok kaynaktan düşer, hedefe ancak teslimde eklenir.
/// </summary>
public static class TransferEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/transfers", async (HttpContext ctx, AppDbContext db, TransferStatus? status, Guid? warehouseId, bool? waitingForMe, int? page, int? pageSize) =>
        {
            var a = await ctx.Access();
            var q = Scoped(db, a).AsNoTracking();
            if (status is TransferStatus s) q = q.Where(t => t.Status == s);
            if (warehouseId is Guid w) q = q.Where(t => t.SourceWarehouseId == w || t.TargetWarehouseId == w);
            if (waitingForMe == true)
                q = a.IsSystemAdmin ? q.Where(t => t.Status == TransferStatus.Sent) : q.Where(t => t.Status == TransferStatus.Sent && a.Warehouses.Contains(t.TargetWarehouseId));
            var p = Math.Max(1, page ?? 1); var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var total = await q.CountAsync();
            var rows = await q.OrderBy(t => t.Status == TransferStatus.Sent ? 0 : t.Status == TransferStatus.Draft ? 1 : 2).ThenByDescending(t => t.CreatedAt)
                .Skip((p - 1) * size).Take(size)
                .Select(t => new { t.Id, t.No, t.Status, Src = t.SourceWarehouse!.Name, Tgt = t.TargetWarehouse!.Name, t.SourceWarehouseId, t.TargetWarehouseId,
                    Items = t.Lines.Count(l => l.ItemId != null), Stock = t.Lines.Count(l => l.StockItemId != null), t.CreatedAt, t.SentAt, t.ReceivedAt, t.CreatedBy })
                .ToListAsync();
            return new PagedResult<TransferListRow>(rows.Select(t => new TransferListRow(t.Id, t.No, t.Status, t.Src, t.Tgt, t.Items, t.Stock, t.CreatedAt, t.SentAt, t.ReceivedAt, t.CreatedBy,
                t.Status == TransferStatus.Draft && a.CanWriteWarehouse(t.SourceWarehouseId),
                t.Status == TransferStatus.Sent && a.CanWriteWarehouse(t.TargetWarehouseId))).ToList(), total, p, size);
        });

        api.MapGet("/transfers/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) => await Detail(db, await ctx.Access(), id));

        api.MapPost("/transfers", async (TransferCreate req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            a.RequireWriteWarehouse(req.SourceWarehouseId);
            if (req.SourceWarehouseId == req.TargetWarehouseId) throw AppException.Invalid("Kaynak ve hedef depo farklı olmalıdır.");
            var src = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == req.SourceWarehouseId && !w.Archived) ?? throw AppException.NotFound("Kaynak depo");
            var tgt = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == req.TargetWarehouseId && !w.Archived) ?? throw AppException.NotFound("Hedef depo");
            if (src.CompanyId != tgt.CompanyId)
                throw AppException.Conflict("Transfer yalnızca aynı şirketin depoları arasında yapılabilir. Şirketler arası devir kapsam dışıdır.");

            var itemIds = (req.ItemIds ?? []).Distinct().ToList();
            var stockLines = (req.StockLines ?? []).Where(l => l.Quantity != 0).GroupBy(l => l.StockItemId)
                .Select(g => new TransferStockLineSave(g.Key, g.Sum(x => x.Quantity))).ToList();
            if (itemIds.Count == 0 && stockLines.Count == 0) throw AppException.Invalid("Transfere en az bir cihaz veya malzeme ekleyin.");
            if (stockLines.Any(l => l.Quantity <= 0)) throw AppException.Invalid("Malzeme miktarları sıfırdan büyük olmalıdır.");

            var items = await db.Items.Where(i => itemIds.Contains(i.Id)).Select(i => new { i.Id, i.Tag, i.WarehouseId, i.Status }).ToListAsync();
            if (items.Count != itemIds.Count) throw AppException.NotFound("Seçilen cihazlardan biri");
            var wrong = items.Where(i => i.WarehouseId != src.Id || i.Status != ItemStatus.InStock).Select(i => i.Tag).ToList();
            if (wrong.Count > 0) throw AppException.Conflict($"Şu cihazlar kaynak depoda hazır durumda değil: {string.Join(", ", wrong)}. Zimmetli cihaz önce iade alınmalıdır.");
            var inDraft = await db.TransferLines.Where(l => l.ItemId != null && itemIds.Contains(l.ItemId.Value))
                .Join(db.Transfers.Where(t => t.Status == TransferStatus.Draft), l => l.TransferId, t => t.Id, (l, t) => t.No).Distinct().ToListAsync();
            if (inDraft.Count > 0) throw AppException.Conflict($"Seçilen cihazlardan bazıları başka bir taslak transferde: {string.Join(", ", inDraft)}.");
            var stockIds = stockLines.Select(l => l.StockItemId).ToList();
            var stockItems = await db.StockItems.Where(s => stockIds.Contains(s.Id) && !s.Archived).ToListAsync();
            if (stockItems.Count != stockIds.Count || stockItems.Any(s => s.CompanyId != src.CompanyId)) throw AppException.NotFound("Seçilen malzemelerden biri");

            var now = DateTimeOffset.UtcNow;
            var t = new Transfer
            {
                Id = Guid.NewGuid(), No = await Counters.DocumentNoAsync(db, "TRF", now), Status = TransferStatus.Draft, CompanyId = src.CompanyId,
                SourceWarehouseId = src.Id, TargetWarehouseId = tgt.Id, Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
                CreatedAt = now, CreatedBy = a.UserName
            };
            foreach (var i in itemIds) t.Lines.Add(new TransferLine { Id = Guid.NewGuid(), ItemId = i });
            foreach (var l in stockLines) t.Lines.Add(new TransferLine { Id = Guid.NewGuid(), StockItemId = l.StockItemId, Quantity = l.Quantity });
            db.Transfers.Add(t);
            Audit.Add(db, a, "Oluşturma", "Transfer", t.Id, $"{t.No} · {src.Name} → {tgt.Name}");
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, a, t.Id));
        });

        api.MapPost("/transfers/{id:guid}/send", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            await using var tx = await db.Database.BeginTransactionAsync();
            var t = await Lock(db, id);
            if (!a.CanReadWarehouse(t.SourceWarehouseId) && !a.CanReadWarehouse(t.TargetWarehouseId)) throw AppException.NotFound("Transfer");
            a.RequireWriteWarehouse(t.SourceWarehouseId);
            if (t.Status == TransferStatus.Sent) return Results.Ok(await Detail(db, a, id)); // tekrar gönderilen istek
            if (t.Status != TransferStatus.Draft) throw AppException.Conflict($"Transfer \"{TransferLabels.Status(t.Status)}\" durumunda; gönderilemez.");

            var lines = await db.TransferLines.Where(l => l.TransferId == id).ToListAsync();
            var itemIds = lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).OrderBy(x => x).ToArray();
            var items = itemIds.Length == 0 ? [] : await db.Items.FromSql($"SELECT *, xmin FROM items WHERE id = ANY({itemIds}) ORDER BY id FOR UPDATE").ToListAsync();
            var wrong = items.Where(i => i.WarehouseId != t.SourceWarehouseId || i.Status != ItemStatus.InStock).Select(i => $"{i.Tag} ({Labels.Status(i.Status)})").ToList();
            if (wrong.Count > 0) throw AppException.Conflict($"Gönderilemedi; şu cihazlar artık kaynak depoda hazır değil: {string.Join(", ", wrong)}.");

            var now = DateTimeOffset.UtcNow;
            var tgtName = await db.Warehouses.Where(w => w.Id == t.TargetWarehouseId).Select(w => w.Name).FirstAsync();
            foreach (var i in items)
            {
                i.Status = ItemStatus.InTransit;
                ItemEndpoints.Event(db, i.Id, "Transfer", $"Transfere gönderildi: {tgtName}", a.UserName, t.No);
            }
            var tr = CultureInfo.GetCultureInfo("tr-TR");
            foreach (var l in lines.Where(l => l.StockItemId != null))
            {
                var after = await StockEndpoints.TryTakeBalance(db, l.StockItemId!.Value, t.SourceWarehouseId, l.Quantity!.Value);
                if (after is null)
                {
                    var s = await db.StockItems.FirstAsync(x => x.Id == l.StockItemId);
                    var have = await db.StockBalances.Where(b => b.StockItemId == s.Id && b.WarehouseId == t.SourceWarehouseId).Select(b => (decimal?)b.Quantity).FirstOrDefaultAsync() ?? 0;
                    throw AppException.Conflict($"Gönderilemedi: kaynak depoda {have.ToString("#,##0.###", tr)} {s.Unit} {s.Name} var, transferde {l.Quantity.Value.ToString("#,##0.###", tr)} isteniyor.");
                }
                db.StockMovements.Add(Movement(StockMovementType.TransferOut, l, t.SourceWarehouseId, -l.Quantity!.Value, after.Value, t, a, await Counters.DocumentNoAsync(db, "STK", now)));
            }
            t.Status = TransferStatus.Sent; t.SentAt = now; t.SentBy = a.UserName;
            Audit.Add(db, a, "Gönderme", "Transfer", t.Id, t.No);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(await Detail(db, a, id));
        });

        api.MapPost("/transfers/{id:guid}/receive", async (Guid id, TransferReceive? req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            await using var tx = await db.Database.BeginTransactionAsync();
            var t = await Lock(db, id);
            if (!a.CanReadWarehouse(t.SourceWarehouseId) && !a.CanReadWarehouse(t.TargetWarehouseId)) throw AppException.NotFound("Transfer");
            if (!a.CanWriteWarehouse(t.TargetWarehouseId)) throw AppException.Forbidden("Teslim almak için hedef depoda yetkiniz olmalıdır.");
            if (t.Status == TransferStatus.Received) return Results.Ok(await Detail(db, a, id));
            if (t.Status != TransferStatus.Sent) throw AppException.Conflict($"Transfer \"{TransferLabels.Status(t.Status)}\" durumunda; teslim alınamaz.");
            if (req?.LocationId is Guid lid && !await db.Locations.AnyAsync(l => l.Id == lid && l.WarehouseId == t.TargetWarehouseId && !l.Archived))
                throw AppException.Invalid("Lokasyon hedef depoya ait değil.");

            var lines = await db.TransferLines.Where(l => l.TransferId == id).ToListAsync();
            var itemIds = lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).OrderBy(x => x).ToArray();
            var items = itemIds.Length == 0 ? [] : await db.Items.FromSql($"SELECT *, xmin FROM items WHERE id = ANY({itemIds}) ORDER BY id FOR UPDATE").ToListAsync();
            var now = DateTimeOffset.UtcNow;
            var tgtName = await db.Warehouses.Where(w => w.Id == t.TargetWarehouseId).Select(w => w.Name).FirstAsync();
            foreach (var i in items)
            {
                if (i.Status != ItemStatus.InTransit) throw AppException.Conflict($"{i.Tag} transferde görünmüyor ({Labels.Status(i.Status)}).");
                i.Status = ItemStatus.InStock; i.WarehouseId = t.TargetWarehouseId; i.LocationId = req?.LocationId;
                ItemEndpoints.Event(db, i.Id, "Transfer", $"Teslim alındı: {tgtName}", a.UserName, t.No);
            }
            foreach (var l in lines.Where(l => l.StockItemId != null))
            {
                var after = await StockEndpoints.AddBalance(db, l.StockItemId!.Value, t.TargetWarehouseId, l.Quantity!.Value);
                db.StockMovements.Add(Movement(StockMovementType.TransferIn, l, t.TargetWarehouseId, l.Quantity!.Value, after, t, a, await Counters.DocumentNoAsync(db, "STK", now)));
            }
            t.Status = TransferStatus.Received; t.ReceivedAt = now; t.ReceivedBy = a.UserName;
            if (!string.IsNullOrWhiteSpace(req?.Note)) t.Note = string.IsNullOrEmpty(t.Note) ? $"Teslim notu: {req.Note.Trim()}" : $"{t.Note}\nTeslim notu: {req.Note.Trim()}";
            Audit.Add(db, a, "Teslim alma", "Transfer", t.Id, t.No);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(await Detail(db, a, id));
        });

        api.MapPost("/transfers/{id:guid}/cancel", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            await using var tx = await db.Database.BeginTransactionAsync();
            var t = await Lock(db, id);
            if (!a.CanReadWarehouse(t.SourceWarehouseId) && !a.CanReadWarehouse(t.TargetWarehouseId)) throw AppException.NotFound("Transfer");
            a.RequireWriteWarehouse(t.SourceWarehouseId);
            if (t.Status == TransferStatus.Cancelled) return Results.Ok(await Detail(db, a, id));
            if (t.Status != TransferStatus.Draft)
                throw AppException.Conflict("Yalnızca gönderilmemiş taslak iptal edilebilir. Gönderilmiş transfer için hedefte teslim alınıp ters yönde yeni transfer açın.");
            t.Status = TransferStatus.Cancelled; t.CancelledAt = DateTimeOffset.UtcNow; t.CancelledBy = a.UserName;
            Audit.Add(db, a, "İptal", "Transfer", t.Id, t.No);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(await Detail(db, a, id));
        });
    }

    static IQueryable<Transfer> Scoped(AppDbContext db, Access a) =>
        a.IsSystemAdmin ? db.Transfers : db.Transfers.Where(t => a.Warehouses.Contains(t.SourceWarehouseId) || a.Warehouses.Contains(t.TargetWarehouseId));

    static async Task<Transfer> Lock(AppDbContext db, Guid id) =>
        await db.Transfers.FromSql($"SELECT *, xmin FROM transfers WHERE id = {id} FOR UPDATE").FirstOrDefaultAsync() ?? throw AppException.NotFound("Transfer");

    static StockMovement Movement(StockMovementType type, TransferLine l, Guid warehouseId, decimal qty, decimal after, Transfer t, Access a, string no) => new()
    {
        Id = Guid.NewGuid(), No = no, At = DateTimeOffset.UtcNow, Type = type, StockItemId = l.StockItemId!.Value, WarehouseId = warehouseId,
        Quantity = qty, BalanceAfter = after, Project = t.No, Note = type == StockMovementType.TransferOut ? "Transfer çıkışı" : "Transfer girişi", UserName = a.UserName
    };

    internal static async Task<TransferDetail> Detail(AppDbContext db, Access a, Guid id)
    {
        var t = await Scoped(db, a).AsNoTracking().Include(x => x.SourceWarehouse).Include(x => x.TargetWarehouse).FirstOrDefaultAsync(x => x.Id == id)
            ?? throw AppException.NotFound("Transfer");
        var items = await db.TransferLines.Where(l => l.TransferId == id && l.ItemId != null).OrderBy(l => l.Item!.Tag)
            .Select(l => new TransferItemLineDto(l.ItemId!.Value, l.Item!.Tag, l.Item.AssetModel!.Manufacturer!.Name + " " + l.Item.AssetModel.Name, l.Item.Serial, l.Item.Status))
            .ToListAsync();
        var stock = await db.TransferLines.Where(l => l.TransferId == id && l.StockItemId != null).OrderBy(l => l.StockItem!.Name)
            .Select(l => new TransferStockLineDto(l.StockItemId!.Value, l.StockItem!.Name, l.StockItem.Unit, l.Quantity!.Value)).ToListAsync();
        var header = new TransferListRow(t.Id, t.No, t.Status, t.SourceWarehouse!.Name, t.TargetWarehouse!.Name, items.Count, stock.Count, t.CreatedAt, t.SentAt, t.ReceivedAt, t.CreatedBy,
            t.Status == TransferStatus.Draft && a.CanWriteWarehouse(t.SourceWarehouseId),
            t.Status == TransferStatus.Sent && a.CanWriteWarehouse(t.TargetWarehouseId));
        return new TransferDetail(header, t.SourceWarehouseId, t.TargetWarehouseId, t.Note, t.SentBy, t.ReceivedBy, t.CancelledBy, items, stock);
    }
}

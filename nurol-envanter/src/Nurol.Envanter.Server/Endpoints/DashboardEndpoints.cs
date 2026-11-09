using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

public static class DashboardEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async (HttpContext ctx, AppDbContext db, Guid? companyId) =>
        {
            var a = await ctx.Access();
            var items = a.Items(db).AsNoTracking();
            var assignments = a.Assignments(db).AsNoTracking();
            var whs = a.WarehousesQuery(db).Where(w => !w.Archived);
            if (companyId is Guid c)
            {
                items = items.Where(i => i.CompanyId == c);
                assignments = assignments.Where(x => x.CompanyId == c);
                whs = whs.Where(w => w.CompanyId == c);
            }

            var byStatus = await items.GroupBy(i => i.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
            int S(ItemStatus s) => byStatus.FirstOrDefault(x => x.Key == s)?.Count ?? 0;

            var missing = assignments.Where(x => !x.Documents.Any(d => d.Kind == DocumentKind.Signed));
            var missingCount = await missing.CountAsync();
            var oldest = await missing.OrderBy(x => x.At).Select(x => (DateTimeOffset?)x.At).FirstOrDefaultAsync();

            var perWh = await items.GroupBy(i => new { i.WarehouseId, i.Status }).Select(g => new { g.Key.WarehouseId, g.Key.Status, Count = g.Count() }).ToListAsync();
            var whList = await whs.OrderBy(w => w.Name).Select(w => new { w.Id, w.Name }).ToListAsync();
            var breakdown = whList.Select(w =>
            {
                int C(ItemStatus s) => perWh.FirstOrDefault(x => x.WarehouseId == w.Id && x.Status == s)?.Count ?? 0;
                return new WarehouseBreakdown(w.Id, w.Name, C(ItemStatus.InStock), C(ItemStatus.Assigned), C(ItemStatus.InMaintenance), C(ItemStatus.InTransit));
            }).ToList();

            var whIds = whList.Select(w => w.Id).ToList();
            var stock = await a.StockItems(db).Where(s => !s.Archived && (companyId == null || s.CompanyId == companyId)).ToListAsync();
            var balances = await db.StockBalances.Where(b => whIds.Contains(b.WarehouseId)).ToListAsync();
            var whOfCompany = await whs.Select(w => new { w.Id, w.Name, w.CompanyId }).ToListAsync();
            var low = stock.Where(s => s.MinLevel > 0)
                .SelectMany(s => whOfCompany.Where(w => w.CompanyId == s.CompanyId)
                    .Select(w => new LowStockRow(s.Id, s.Name, s.Unit, w.Name, balances.FirstOrDefault(b => b.StockItemId == s.Id && b.WarehouseId == w.Id)?.Quantity ?? 0, s.MinLevel)))
                .Where(r => r.Quantity < r.MinLevel)
                .OrderBy(r => r.Quantity / r.MinLevel).Take(8).ToList();

            var recentAssign = await assignments.OrderByDescending(x => x.At).Take(10)
                .Select(x => new ActivityRow(x.At, x.Kind == AssignmentKind.Issue ? "Zimmet" : "İade", x.No, x.Employee!.FullName + " · " + x.Lines.Count + " cihaz", x.UserName)).ToListAsync();
            var recentStock = await a.StockMovements(db).Where(m => whIds.Contains(m.WarehouseId)).OrderByDescending(m => m.At).Take(10)
                .Select(m => new { m.At, m.Type, m.No, Name = m.StockItem!.Name, m.Quantity, m.StockItem.Unit, Wh = m.Warehouse!.Name, m.UserName }).ToListAsync();
            var activity = recentAssign
                .Concat(recentStock.Select(m => new ActivityRow(m.At, Labels.Movement(m.Type), m.No, $"{m.Name}, {Math.Abs(m.Quantity):0.###} {m.Unit} · {m.Wh}", m.UserName)))
                .OrderByDescending(x => x.At).Take(10).ToList();

            var transfers = a.IsSystemAdmin ? db.Transfers : db.Transfers.Where(t => a.Warehouses.Contains(t.SourceWarehouseId) || a.Warehouses.Contains(t.TargetWarehouseId));
            if (companyId is Guid cc) transfers = transfers.Where(t => t.CompanyId == cc);
            var pending = await transfers.CountAsync(t => t.Status == TransferStatus.Sent);
            var recentTransfers = await transfers.Where(t => t.SentAt != null).OrderByDescending(t => t.ReceivedAt ?? t.SentAt).Take(10)
                .Select(t => new { t.No, t.Status, At = t.ReceivedAt ?? t.SentAt!.Value, Src = t.SourceWarehouse!.Name, Tgt = t.TargetWarehouse!.Name, By = t.ReceivedBy ?? t.SentBy ?? "" }).ToListAsync();
            activity = activity.Concat(recentTransfers.Select(t => new ActivityRow(t.At, t.Status == TransferStatus.Received ? "Transfer teslim alındı" : "Transfer gönderildi", t.No, $"{t.Src} → {t.Tgt}", t.By)))
                .OrderByDescending(x => x.At).Take(10).ToList();

            return new DashboardDto(byStatus.Sum(x => x.Count) - S(ItemStatus.Retired), S(ItemStatus.InStock), S(ItemStatus.Assigned), S(ItemStatus.InMaintenance),
                S(ItemStatus.InTransit), S(ItemStatus.Retired), missingCount, oldest, pending, breakdown, low, activity);
        });
    }
}

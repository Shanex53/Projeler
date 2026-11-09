using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Server.Services;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

/// <summary>
/// SNMP ile izlenen yazıcılar. Görme: yazıcının deposunda okuma yetkisi. Ekleme/düzenleme/toner eşleme/stoktan düşme: depoda yazma yetkisi.
/// </summary>
public static class PrinterEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/printers", async (HttpContext ctx, AppDbContext db, PrinterMonitor m, Guid? warehouseId, bool? onlyWarnings) =>
        {
            var a = await ctx.Access();
            var q = PrinterMonitor.Scoped(db, a).AsNoTracking();
            if (warehouseId is Guid w) q = q.Where(p => p.WarehouseId == w);
            var rows = await m.RowsAsync(a, q);
            return onlyWarnings == true ? rows.Where(r => r.Warnings.Count > 0 || r.Online == false).ToList() : rows;
        });

        api.MapGet("/printers/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, PrinterMonitor m) => await Detail(db, m, await ctx.Access(), id));

        api.MapPost("/printers/probe", async (PrinterProbeRequest req, HttpContext ctx, AppDbContext db, IPrinterProbe probe) =>
        {
            var a = await ctx.Access(); a.RequireWrite();
            var community = req.Community;
            if (string.IsNullOrEmpty(community) && req.PrinterId is Guid pid)
            {
                var p = await PrinterMonitor.Scoped(db, a).FirstOrDefaultAsync(x => x.Id == pid) ?? throw AppException.NotFound("Yazıcı");
                community = p.Community;
            }
            Validate(req.Host, req.Port, req.SnmpVersion);
            try
            {
                var s = await probe.ProbeAsync(req.Host.Trim(), req.Port, req.SnmpVersion, string.IsNullOrEmpty(community) ? "public" : community, ctx.RequestAborted);
                var supplies = s.Supplies.Select(x =>
                {
                    var st = new PrinterMonitor.StoredSupply(PrinterMonitor.KeyOf(x), x.Index, x.Description, x.Color, x.TypeCode, x.Max, x.Level);
                    var (pct, level, state) = PrinterMonitor.Interpret(st, 15);
                    return new PrinterSupplyDto(st.Key, x.Index, x.Description, x.Color, PrinterMonitor.TypeLabel(x.TypeCode), pct, level, state, PrinterMonitor.Monitored(x.TypeCode), null, null, null, null, null, false);
                }).ToList();
                var msg = supplies.Count == 0
                    ? "Yazıcı yanıt verdi ancak sarf bilgisi (Printer-MIB) göndermiyor. Sayaç ve model izlenebilir; toner seviyesi görünmeyebilir."
                    : $"Bağlantı başarılı: {supplies.Count} sarf yuvası okundu.";
                return new PrinterProbeResult(true, msg, s.Model, s.SysName, s.SerialNumber, s.PageCount, supplies);
            }
            catch (PrinterProbeException ex) { return new PrinterProbeResult(false, ex.Message, null, null, null, null, []); }
        });

        api.MapPost("/printers", async (PrinterSave req, HttpContext ctx, AppDbContext db, PrinterMonitor m, PrinterOptions o) =>
        {
            var a = await ctx.Access();
            a.RequireWriteWarehouse(req.WarehouseId);
            Validate(req.Host, req.Port, req.SnmpVersion);
            await CheckHost(req.Host, o, ctx.RequestAborted);
            var wh = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == req.WarehouseId && !w.Archived) ?? throw AppException.NotFound("Depo");
            if (await db.Printers.AnyAsync(p => p.Host == req.Host.Trim() && p.Port == req.Port))
                throw AppException.Conflict($"{req.Host.Trim()} adresinde kayıtlı bir yazıcı zaten var.");
            var p = new Printer { Id = Guid.NewGuid(), CompanyId = wh.CompanyId, CreatedAt = DateTimeOffset.UtcNow, Community = "public" };
            Apply(p, req, wh);
            db.Printers.Add(p);
            Audit.Add(db, a, "Oluşturma", "Yazıcı", p.Id, $"{p.Name} · {p.Host}");
            await db.SaveChangesAsync();
            p.Warehouse = wh;
            await m.PollAsync(p, a.UserName);
            return Results.Ok(await Detail(db, m, a, p.Id));
        });

        api.MapPut("/printers/{id:guid}", async (Guid id, PrinterSave req, HttpContext ctx, AppDbContext db, PrinterMonitor m, PrinterOptions o) =>
        {
            var a = await ctx.Access();
            var p = await PrinterMonitor.Scoped(db, a).Include(x => x.Warehouse).FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("Yazıcı");
            a.RequireWriteWarehouse(p.WarehouseId);
            a.RequireWriteWarehouse(req.WarehouseId);
            Validate(req.Host, req.Port, req.SnmpVersion);
            if (p.Host != req.Host.Trim()) await CheckHost(req.Host, o, ctx.RequestAborted);
            var wh = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == req.WarehouseId && !w.Archived) ?? throw AppException.NotFound("Depo");
            if (wh.CompanyId != p.CompanyId) throw AppException.Conflict("Yazıcı başka şirketin deposuna taşınamaz.");
            if ((p.Host != req.Host.Trim() || p.Port != req.Port) && await db.Printers.AnyAsync(x => x.Id != id && x.Host == req.Host.Trim() && x.Port == req.Port))
                throw AppException.Conflict($"{req.Host.Trim()} adresinde kayıtlı başka bir yazıcı var.");
            Apply(p, req, wh);
            Audit.Add(db, a, "Güncelleme", "Yazıcı", p.Id, p.Name);
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, m, a, id));
        });

        api.MapDelete("/printers/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var p = await PrinterMonitor.Scoped(db, a).FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("Yazıcı");
            a.RequireWriteWarehouse(p.WarehouseId);
            db.Printers.Remove(p);
            Audit.Add(db, a, "Silme", "Yazıcı", p.Id, $"{p.Name} · {p.Host}");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        api.MapPost("/printers/{id:guid}/refresh", async (Guid id, HttpContext ctx, AppDbContext db, PrinterMonitor m) =>
        {
            var a = await ctx.Access();
            var p = await PrinterMonitor.Scoped(db, a).Include(x => x.Warehouse).FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("Yazıcı");
            await m.PollAsync(p, a.UserName, ctx.RequestAborted);
            return await Detail(db, m, a, id);
        });

        api.MapPut("/printers/{id:guid}/supplies/{key}/toner", async (Guid id, string key, PrinterTonerMap req, HttpContext ctx, AppDbContext db, PrinterMonitor m) =>
        {
            var a = await ctx.Access();
            var p = await PrinterMonitor.Scoped(db, a).FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("Yazıcı");
            a.RequireWriteWarehouse(p.WarehouseId);
            if (PrinterMonitor.Stored(p).All(s => s.Key != key)) throw AppException.NotFound("Sarf yuvası");
            StockItem? toner = null;
            if (req.StockItemId is Guid sid)
            {
                toner = await db.StockItems.FirstOrDefaultAsync(s => s.Id == sid && !s.Archived) ?? throw AppException.NotFound("Malzeme");
                if (toner.CompanyId != p.CompanyId) throw AppException.Conflict("Toner, yazıcıyla aynı şirketin malzemesi olmalıdır.");
            }
            // Aynı modeldeki (ve aynı şirketteki, yetkili olunan) yazıcılara da uygulanabilir.
            var targets = new List<Printer> { p };
            if (req.ApplyToSameModel && !string.IsNullOrEmpty(p.Model))
                targets.AddRange((await PrinterMonitor.Scoped(db, a).Where(x => x.Id != id && x.CompanyId == p.CompanyId && x.Model == p.Model).ToListAsync())
                    .Where(x => a.CanWriteWarehouse(x.WarehouseId) && PrinterMonitor.Stored(x).Any(s => s.Key == key)));
            var ids = targets.Select(t => t.Id).ToList();
            var existing = await db.PrinterSupplyMaps.Where(x => ids.Contains(x.PrinterId) && x.SupplyKey == key).ToListAsync();
            foreach (var t in targets)
            {
                var row = existing.FirstOrDefault(x => x.PrinterId == t.Id);
                if (toner is null) { if (row is not null) db.PrinterSupplyMaps.Remove(row); continue; }
                if (row is null) db.PrinterSupplyMaps.Add(new PrinterSupplyMap { Id = Guid.NewGuid(), PrinterId = t.Id, SupplyKey = key, StockItemId = toner.Id });
                else row.StockItemId = toner.Id;
            }
            Audit.Add(db, a, "Toner eşleme", "Yazıcı", p.Id, $"{key} → {toner?.Name ?? "kaldırıldı"} · {targets.Count} yazıcı");
            await db.SaveChangesAsync();
            return Results.Ok(new { updated = targets.Count });
        });

        // Parça numarasından önerilen tonerleri, tonersiz yuvalara toplu olarak uygular (yalnızca tek eşleşme olan yuvalar).
        api.MapPost("/printers/apply-suggestions", async (HttpContext ctx, AppDbContext db, PrinterMonitor m, Guid? printerId) =>
        {
            var a = await ctx.Access(); a.RequireWrite();
            var q = PrinterMonitor.Scoped(db, a).AsNoTracking();
            if (printerId is Guid pid) q = q.Where(p => p.Id == pid);
            var rows = (await m.RowsAsync(a, q)).Where(r => r.CanManage).ToList();
            var applied = 0;
            foreach (var r in rows)
                foreach (var s in r.Supplies.Where(s => s.StockItemId is null && s.SuggestedStockItemId is not null))
                {
                    db.PrinterSupplyMaps.Add(new PrinterSupplyMap { Id = Guid.NewGuid(), PrinterId = r.Id, SupplyKey = s.Key, StockItemId = s.SuggestedStockItemId!.Value });
                    applied++;
                }
            if (applied > 0)
            {
                Audit.Add(db, a, "Toner önerilerini uygulama", "Yazıcı", printerId, $"{applied} yuva");
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { applied });
        });

        api.MapPost("/printers/{id:guid}/supplies/{key}/replace", async (Guid id, string key, PrinterReplace req, HttpContext ctx, AppDbContext db, PrinterMonitor m) =>
        {
            var a = await ctx.Access();
            var p = await PrinterMonitor.Scoped(db, a).Include(x => x.Warehouse).FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("Yazıcı");
            a.RequireWriteWarehouse(p.WarehouseId);
            var supply = PrinterMonitor.Stored(p).FirstOrDefault(s => s.Key == key) ?? throw AppException.NotFound("Sarf yuvası");
            string text = $"{supply.Description} değiştirildi";
            if (req.ConsumeFromStock)
            {
                var map = await db.PrinterSupplyMaps.Include(x => x.StockItem).FirstOrDefaultAsync(x => x.PrinterId == id && x.SupplyKey == key)
                    ?? throw AppException.Conflict("Stoktan düşmek için önce bu yuvaya kullanılacak toneri seçin.");
                var key2 = ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
                var mv = await StockEndpoints.Move(db, a, new StockMovementRequest(map.StockItemId, p.WarehouseId, StockMovementType.Out, 1, null,
                    $"Yazıcı: {p.Name}", string.IsNullOrWhiteSpace(req.Note) ? $"{supply.Description} değişimi" : req.Note.Trim()), key2);
                text += $"; stoktan düşüldü: {map.StockItem!.Name} ({mv.No}), kalan {mv.BalanceAfter:0.###} {map.StockItem.Unit}";
            }
            var st = await db.PrinterSupplyStates.FindAsync(id, key);
            if (st is not null) { st.PendingReplacement = false; st.LastReplacedAt = DateTimeOffset.UtcNow; }
            db.PrinterEvents.Add(new PrinterEvent { Id = Guid.NewGuid(), PrinterId = id, At = DateTimeOffset.UtcNow, Type = "Toner değişimi", Text = text, UserName = a.UserName });
            await db.SaveChangesAsync();
            return await Detail(db, m, a, id);
        });

        api.MapGet("/printers/toner-needs", async (HttpContext ctx, AppDbContext db, PrinterMonitor m) =>
        {
            var a = await ctx.Access();
            var rows = await m.RowsAsync(a, PrinterMonitor.Scoped(db, a).AsNoTracking().Where(p => p.IsActive));
            var used = rows.SelectMany(r => r.Supplies.Where(s => s.StockItemId != null).Select(s => (Printer: r, Supply: s))).GroupBy(x => x.Supply.StockItemId!.Value).ToList();
            var ids = used.Select(g => g.Key).ToList();
            var items = await db.StockItems.Where(s => ids.Contains(s.Id)).ToListAsync();
            var whs = await a.WarehousesQuery(db).Where(w => !w.Archived).Select(w => new { w.Id, w.Name, w.CompanyId }).ToListAsync();
            var bal = await db.StockBalances.Where(b => ids.Contains(b.StockItemId)).ToListAsync();
            return used.Select(g =>
            {
                var s = items.First(x => x.Id == g.Key);
                var balances = whs.Where(w => w.CompanyId == s.CompanyId)
                    .Select(w => new StockBalanceDto(w.Id, w.Name, bal.FirstOrDefault(b => b.StockItemId == s.Id && b.WarehouseId == w.Id)?.Quantity ?? 0)).ToList();
                var total = balances.Sum(b => b.Quantity);
                var low = g.Count(x => x.Supply.State is "low" or "empty");
                var status = total == 0 ? (low > 0 ? "Stok yok, ihtiyaç var" : "Stok yok")
                    : total < low ? "Stok yetersiz" : s.MinLevel > 0 && total < s.MinLevel ? "Kritik seviyede" : "Yeterli";
                return new TonerNeedRow(s.Id, s.Code, s.Name, s.Unit, s.MinLevel, g.Select(x => x.Printer.Id).Distinct().Count(), low, total, balances, status,
                    g.Select(x => x.Printer.Name).Distinct().OrderBy(n => n).ToList());
            }).OrderByDescending(r => r.LowCount).ThenBy(r => r.Name).ToList();
        });
    }

    /// <summary>Adres çözümlenmeli ve izin verilen yazıcı ağlarında olmalı.</summary>
    static async Task CheckHost(string host, PrinterOptions o, CancellationToken ct)
    {
        try { await SnmpPrinterProbe.Resolve(host, o, ct); }
        catch (PrinterProbeException pe) { throw AppException.Invalid(pe.Message, new() { ["host"] = pe.Message }); }
    }

    static void Validate(string host, int port, string version)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Trim().Length > 253 || host.Any(char.IsWhiteSpace))
            throw AppException.Invalid("IP adresi veya ana bilgisayar adı girin.", new() { ["host"] = "Geçerli bir adres girin." });
        if (port is < 1 or > 65535) throw AppException.Invalid("Port 1-65535 arasında olmalıdır.");
        if (version is not ("v1" or "v2c")) throw AppException.Invalid("SNMP sürümü v1 veya v2c olmalıdır.");
    }

    static void Apply(Printer p, PrinterSave r, Warehouse wh)
    {
        DefinitionEndpoints.Require(r.Name, "Yazıcı adı");
        if (r.LowThreshold is < 1 or > 90) throw AppException.Invalid("Uyarı eşiği %1 ile %90 arasında olmalıdır.");
        p.Name = r.Name.Trim(); p.Host = r.Host.Trim(); p.Port = r.Port; p.SnmpVersion = r.SnmpVersion; p.WarehouseId = wh.Id;
        p.Location = string.IsNullOrWhiteSpace(r.Location) ? null : r.Location.Trim(); p.LowThreshold = r.LowThreshold; p.IsActive = r.IsActive;
        if (!string.IsNullOrEmpty(r.Community)) p.Community = r.Community;
    }

    static async Task<PrinterDetail> Detail(AppDbContext db, PrinterMonitor m, Access a, Guid id)
    {
        var p = await PrinterMonitor.Scoped(db, a).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("Yazıcı");
        var row = (await m.RowsAsync(a, PrinterMonitor.Scoped(db, a).AsNoTracking().Where(x => x.Id == id)))[0];
        var events = await db.PrinterEvents.Where(e => e.PrinterId == id).OrderByDescending(e => e.At).Take(50)
            .Select(e => new PrinterEventDto(e.At, e.Type, e.Text, e.UserName)).ToListAsync();
        var since = DateOnly.FromDateTime(TimeZones.ToLocal(DateTimeOffset.UtcNow)).AddDays(-60);
        var days = await db.PrinterCounters.Where(c => c.PrinterId == id && c.Day >= since).OrderBy(c => c.Day).ToListAsync();
        var prevBefore = await db.PrinterCounters.Where(c => c.PrinterId == id && c.Day < since).OrderByDescending(c => c.Day).Select(c => (long?)c.LastCount).FirstOrDefaultAsync();
        var counters = new List<PrinterCounterDay>();
        long? prev = prevBefore;
        foreach (var d in days)
        {
            var baseline = prev ?? d.FirstCount;
            long? pages = d.LastCount >= baseline ? d.LastCount - baseline : null; // sayaç sıfırlanmışsa bilinmiyor
            counters.Add(new PrinterCounterDay(d.Day, d.LastCount, pages));
            prev = d.LastCount;
        }
        var cutoff = DateOnly.FromDateTime(TimeZones.ToLocal(DateTimeOffset.UtcNow)).AddDays(-30);
        long? last30 = counters.Any(c => c.Day > cutoff) ? counters.Where(c => c.Day > cutoff).Sum(c => c.Pages ?? 0) : null;
        return new PrinterDetail(row, p.SerialNumber, p.SysName, p.Port, p.SnmpVersion, p.LowThreshold, !string.IsNullOrEmpty(p.Community),
            events, counters.OrderByDescending(c => c.Day).ToList(), last30);
    }
}

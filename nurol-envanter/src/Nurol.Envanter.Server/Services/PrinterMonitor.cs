using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Services;

/// <summary>
/// Yazıcı okumalarını yorumlar ve kaydeder:
/// - Seviye: yüzde, "var" (-3), bilinmiyor (-2/-1). Eşiğin altı "low", 0 "empty".
/// - Azalma dönemi: yuva eşiğin altına ilk düştüğünde olay + e-posta (bir kez); seviye yükselince dönem kapanır.
/// - Değişim algılama: seviye en az 30 puan artıp %70'in üzerine çıkarsa "toner değişimi algılandı"; stoktan düşme onayı beklenir.
/// - Günlük sayaç: gün sonu sayfa sayısı saklanır; aylık/aralık baskı sayısı bundan hesaplanır.
/// </summary>
public class PrinterMonitor(AppDbContext db, IPrinterProbe probe, PrinterOptions o, Notifier notifier, ILogger<PrinterMonitor> log)
{
    public record StoredSupply(string Key, int Index, string Description, string? Color, int TypeCode, int Max, int Level);

    static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Toner, mürekkep, drum ve atık toner izlenir; diğer sarflar (fuser vb.) yalnızca gösterilir.</summary>
    public static bool Monitored(int type) => type is 3 or 4 or 5 or 6 or 9 or 21;

    public static string TypeLabel(int type) => type switch
    {
        3 => "Toner", 4 => "Atık toner", 5 => "Mürekkep", 6 => "Mürekkep kartuşu", 9 => "Drum (OPC)", 15 => "Fırın (fuser)", 21 => "Toner kartuşu",
        _ => "Sarf"
    };

    public static string KeyOf(SupplyReading s)
    {
        var basis = !string.IsNullOrWhiteSpace(s.Color) && s.TypeCode is 3 or 5 or 6 or 21 ? "renk:" + s.Color : $"{s.TypeCode}:{s.Description}";
        return FieldRules.Slug(basis.Replace(':', ' ')) is { Length: > 0 } k ? k[..Math.Min(k.Length, 60)] : $"sarf_{s.Index}";
    }

    public static int? Percent(int max, int level)
    {
        if (level < 0) return null;
        if (max > 0) return Math.Clamp((int)Math.Round(level * 100.0 / max), 0, 100);
        return level <= 100 ? level : null; // bazı yazıcılar kapasiteyi bilinmiyor (-2) verip seviyeyi yüzde olarak gönderir
    }

    public static (int? Percent, string Level, string State) Interpret(StoredSupply s, int threshold)
    {
        var p = Percent(s.Max, s.Level);
        if (p is int v) return (v, $"%{v}", v == 0 ? "empty" : v <= threshold ? "low" : "ok");
        return s.Level == -3 ? (null, "Var", "ok") : (null, "Bilinmiyor", "unknown");
    }

    public static List<StoredSupply> Stored(Printer p) =>
        JsonSerializer.Deserialize<List<StoredSupply>>(p.Supplies, JsonSerializerOptions.Web) ?? [];

    /// <summary>Okumayı uygular. Okuma başarısızsa yalnızca bağlantı durumu güncellenir; son bilinen seviyeler korunur.</summary>
    public async Task ApplyAsync(Printer p, PrinterSnapshot? snap, string? error, string userName, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        p.LastPolledAt = now;
        if (snap is null)
        {
            if (p.Online != false) Event(p, "Ulaşılamıyor", error ?? "Yanıt yok", userName);
            p.Online = false; p.LastError = error;
            return;
        }
        if (p.Online == false) Event(p, "Çevrimiçi", "Yazıcı tekrar yanıt veriyor", userName);
        p.Online = true; p.LastError = null; p.LastSuccessAt = now;
        p.Model = snap.Model ?? p.Model; p.SysName = snap.SysName; p.SerialNumber = snap.SerialNumber ?? p.SerialNumber;
        var stored = snap.Supplies.Select(s => new StoredSupply(KeyOf(s), s.Index, s.Description, s.Color, s.TypeCode, s.Max, s.Level)).ToList();
        // Aynı anahtarı taşıyan iki yuva olursa (ör. iki "black") ikinciye sıra eklenir.
        for (var i = 0; i < stored.Count; i++)
            if (stored.Take(i).Any(x => x.Key == stored[i].Key)) stored[i] = stored[i] with { Key = $"{stored[i].Key}_{stored[i].Index}" };
        p.Supplies = JsonSerializer.Serialize(stored, JsonSerializerOptions.Web);

        if (snap.PageCount is long count)
        {
            p.PageCount = count;
            var day = DateOnly.FromDateTime(TimeZones.ToLocal(now));
            var c = await db.PrinterCounters.FindAsync([p.Id, day], ct);
            if (c is null) db.PrinterCounters.Add(new PrinterCounter { PrinterId = p.Id, Day = day, FirstCount = count, LastCount = count });
            else c.LastCount = count;
        }

        var states = await db.PrinterSupplyStates.Where(s => s.PrinterId == p.Id).ToDictionaryAsync(s => s.SupplyKey, ct);
        var maps = await db.PrinterSupplyMaps.Where(m => m.PrinterId == p.Id).Include(m => m.StockItem).ToDictionaryAsync(m => m.SupplyKey, ct);
        var companyStock = await db.StockItems.Where(x => x.CompanyId == p.CompanyId && !x.Archived).ToListAsync(ct);
        foreach (var s in stored.Where(s => Monitored(s.TypeCode)))
        {
            var (pct, _, state) = Interpret(s, p.LowThreshold);
            if (!states.TryGetValue(s.Key, out var st))
            {
                st = new PrinterSupplyState { PrinterId = p.Id, SupplyKey = s.Key };
                db.PrinterSupplyStates.Add(st);
                states[s.Key] = st;
            }
            if (st.LastPercent is int prev && pct is int cur && cur - prev >= 30 && cur >= 70)
            {
                st.PendingReplacement = true;
                st.LastReplacedAt = now;
                Event(p, "Değişim algılandı", $"{s.Description}: %{prev} → %{cur}. Stoktan düşmek için yazıcı sayfasında onaylayın.", userName);
            }
            if (state is "low" or "empty")
            {
                if (st.LowSince is null)
                {
                    st.LowSince = now;
                    var map = maps.GetValueOrDefault(s.Key);
                    var suggestion = map is null ? TonerParts.Suggest(s.Description, companyStock) : null;
                    var shown = map?.StockItem ?? suggestion;
                    var (inWh, total) = shown is null ? (null, null) : await StockOf(shown.Id, p.WarehouseId, ct);
                    var msg = WarningText(s, pct, state, map?.StockItem, inWh, total, p, suggestion);
                    Event(p, state == "empty" ? "Sarf bitti" : "Sarf azaldı", msg, userName);
                    await notifier.SupplyLowAsync(p, s.Description, pct, state, msg, $"supply-low:{p.Id}:{s.Key}:{now:yyyyMMddHHmmss}");
                }
            }
            else if (state == "ok") st.LowSince = null;
            if (pct is not null) st.LastPercent = pct;
        }
    }

    public async Task<(decimal? InWarehouse, decimal? Total)> StockOf(Guid stockItemId, Guid warehouseId, CancellationToken ct = default)
    {
        var b = await db.StockBalances.Where(x => x.StockItemId == stockItemId).Select(x => new { x.WarehouseId, x.Quantity }).ToListAsync(ct);
        return (b.FirstOrDefault(x => x.WarehouseId == warehouseId)?.Quantity ?? 0, b.Sum(x => x.Quantity));
    }

    /// <summary>“Siyah toner %8. Kullanılacak toner: HP 151A (SRF-TNR-014) — Ankara Merkez Depo'da 4 adet.”</summary>
    public static string WarningText(StoredSupply s, int? pct, string state, StockItem? toner, decimal? inWh, decimal? total, Printer p, StockItem? suggestion = null)
    {
        var what = s.TypeCode == 4
            ? (state == "empty" ? $"{s.Description} dolu" : $"{s.Description} dolmak üzere (kalan kapasite %{pct})")
            : state == "empty" ? $"{s.Description} bitti" : $"{s.Description} %{pct}";
        if (s.TypeCode == 4) return what + ". Atık toner kutusunu değiştirin.";
        var part = TonerParts.Display(s.Description);
        if (toner is null && suggestion is null)
            return what + ". Bu yuva için kullanılacak toner tanımlanmamış" + (part is null ? "" : $" (yazıcının bildirdiği parça: {part}; stokta eşleşen malzeme yok)") + "; yazıcı sayfasından seçin.";
        var t = toner ?? suggestion!;
        var unit = t.Unit;
        var stock = inWh > 0
            ? $"{p.Warehouse?.Name ?? "depoda"}: {inWh.Value.ToString("#,##0.###", Tr)} {unit}"
            : total > 0 ? $"bu depoda yok, diğer depolarda toplam {total.Value.ToString("#,##0.###", Tr)} {unit}" : "stokta yok";
        return toner is not null
            ? $"{what}. Kullanılacak toner: {t.Name} ({t.Code}) — {stock}."
            : $"{what}. Önerilen toner: {t.Name} ({t.Code}), yazıcının bildirdiği {part} parçasına göre — {stock}. Yazıcı sayfasından onaylayın.";
    }

    void Event(Printer p, string type, string text, string user) =>
        db.PrinterEvents.Add(new PrinterEvent { Id = Guid.NewGuid(), PrinterId = p.Id, At = DateTimeOffset.UtcNow, Type = type, Text = text, UserName = user });

    /// <summary>Tek yazıcıyı okur ve kaydeder (SaveChanges dahil).</summary>
    public async Task PollAsync(Printer p, string userName, CancellationToken ct = default)
    {
        PrinterSnapshot? snap = null; string? err = null;
        try { snap = await probe.ProbeAsync(p.Host, p.Port, p.SnmpVersion, p.Community, ct); }
        catch (PrinterProbeException ex) { err = ex.Message; }
        await ApplyAsync(p, snap, err, userName, ct);
        await db.SaveChangesAsync(ct);
    }

    // ---- Görünüm modelleri ----

    public async Task<List<PrinterListRow>> RowsAsync(Access a, IQueryable<Printer> q, CancellationToken ct = default)
    {
        var printers = await q.Include(p => p.Warehouse).OrderBy(p => p.Warehouse!.Name).ThenBy(p => p.Name).ToListAsync(ct);
        var ids = printers.Select(p => p.Id).ToList();
        var maps = await db.PrinterSupplyMaps.Where(m => ids.Contains(m.PrinterId)).Include(m => m.StockItem).ToListAsync(ct);
        var states = await db.PrinterSupplyStates.Where(s => ids.Contains(s.PrinterId)).ToListAsync(ct);
        var companyIds = printers.Select(p => p.CompanyId).Distinct().ToList();
        var stockByCompany = (await db.StockItems.Where(s => companyIds.Contains(s.CompanyId) && !s.Archived).ToListAsync(ct)).ToLookup(s => s.CompanyId);
        var stockIds = maps.Select(m => m.StockItemId).Concat(stockByCompany.SelectMany(g => g).Select(s => s.Id)).Distinct().ToList();
        var balances = await db.StockBalances.Where(b => stockIds.Contains(b.StockItemId)).ToListAsync(ct);
        return printers.Select(p =>
        {
            var supplies = new List<PrinterSupplyDto>();
            var warnings = new List<PrinterWarning>();
            foreach (var s in Stored(p))
            {
                var (pct, level, state) = Interpret(s, p.LowThreshold);
                var map = maps.FirstOrDefault(m => m.PrinterId == p.Id && m.SupplyKey == s.Key);
                var suggestion = map is null && Monitored(s.TypeCode) && s.TypeCode != 4 ? TonerParts.Suggest(s.Description, stockByCompany[p.CompanyId]) : null;
                var shownId = map?.StockItemId ?? suggestion?.Id;
                decimal? inWh = shownId is null ? null : balances.FirstOrDefault(b => b.StockItemId == shownId && b.WarehouseId == p.WarehouseId)?.Quantity ?? 0;
                decimal? total = shownId is null ? null : balances.Where(b => b.StockItemId == shownId).Sum(b => b.Quantity);
                var pending = states.FirstOrDefault(x => x.PrinterId == p.Id && x.SupplyKey == s.Key)?.PendingReplacement == true;
                supplies.Add(new PrinterSupplyDto(s.Key, s.Index, s.Description, s.Color, TypeLabel(s.TypeCode), pct, level, state, Monitored(s.TypeCode),
                    map?.StockItemId, map?.StockItem?.Name, map?.StockItem?.Code, map is null ? null : inWh, map is null ? null : total, pending,
                    TonerParts.Display(s.Description), suggestion?.Id, suggestion is null ? null : $"{suggestion.Name} ({suggestion.Code})"));
                if (Monitored(s.TypeCode) && state is "low" or "empty" && p.IsActive)
                    warnings.Add(new PrinterWarning(p.Id, p.Name, p.Location, s.Key, s.Description, pct, state == "empty" || pct <= 5 ? "Kritik" : "Uyarı",
                        WarningText(s, pct, state, map?.StockItem, inWh, total, p, suggestion), map?.StockItemId ?? suggestion?.Id,
                        map?.StockItem?.Name ?? suggestion?.Name, inWh, total));
            }
            return new PrinterListRow(p.Id, p.Name, p.Host, p.Location, p.WarehouseId, p.Warehouse!.Name, p.Model, p.IsActive, p.Online, p.LastPolledAt,
                p.LastSuccessAt, p.LastError, p.PageCount, supplies, warnings, a.CanWriteWarehouse(p.WarehouseId));
        }).ToList();
    }

    public static IQueryable<Printer> Scoped(AppDbContext db, Access a) =>
        a.IsSystemAdmin ? db.Printers : db.Printers.Where(p => a.Warehouses.Contains(p.WarehouseId));
}

/// <summary>
/// Periyodik okuma. Çoklu pod'da aynı anda yalnızca biri okur (PostgreSQL advisory lock).
/// Okumalar paralel yapılır; sonuçlar tek seferde kaydedilir.
/// </summary>
public class PrinterPoller(IServiceScopeFactory scopes, PrinterOptions o, ILogger<PrinterPoller> log) : BackgroundService
{
    const long LockKey = 472030;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!o.Enabled) return;
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, o.PollMinutes)));
        do
        {
            try { await CycleAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Yazıcı okuma döngüsü başarısız"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task<int> CycleAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var probe = scope.ServiceProvider.GetRequiredService<IPrinterProbe>();
        var monitor = scope.ServiceProvider.GetRequiredService<PrinterMonitor>();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var got = (await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").ToListAsync(ct))[0];
            if (!got) return 0;
            try
            {
                var printers = await db.Printers.Include(p => p.Warehouse).Where(p => p.IsActive).ToListAsync(ct);
                using var gate = new SemaphoreSlim(Math.Max(1, o.Parallelism));
                var results = await Task.WhenAll(printers.Select(async p =>
                {
                    await gate.WaitAsync(ct);
                    try { return (p, Snap: await probe.ProbeAsync(p.Host, p.Port, p.SnmpVersion, p.Community, ct), Err: (string?)null); }
                    catch (PrinterProbeException ex) { return (p, Snap: (PrinterSnapshot?)null, Err: ex.Message); }
                    finally { gate.Release(); }
                }));
                foreach (var r in results) await monitor.ApplyAsync(r.p, r.Snap, r.Err, "otomatik", ct);
                await db.SaveChangesAsync(ct);
                return results.Length;
            }
            finally { await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None); }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}

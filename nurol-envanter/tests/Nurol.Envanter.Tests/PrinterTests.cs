using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Server.Services;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Tests;

/// <summary>
/// Gerçek UDP üzerinden yanıt veren küçük bir SNMP ajanı (Printer-MIB alt kümesi). Değerler test sırasında değiştirilebilir;
/// böylece toner azalması, değişimi ve yazıcının ulaşılamaz olması uçtan uca sınanır.
/// </summary>
public sealed class FakeSnmpAgent : IDisposable
{
    readonly UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    readonly CancellationTokenSource cts = new();
    readonly ConcurrentDictionary<string, ISnmpData> data = new();
    public string Community { get; }
    public int Port => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    public bool Silent { get; set; }

    public FakeSnmpAgent(string community, string model, long pages, params (string Desc, string Color, int Type, int Level)[] supplies)
    {
        Community = community;
        Set("1.3.6.1.2.1.1.1.0", new OctetString(model));
        Set("1.3.6.1.2.1.1.5.0", new OctetString("NPI-" + community.ToUpperInvariant()));
        Set("1.3.6.1.2.1.25.3.2.1.3.1", new OctetString(model));
        Set("1.3.6.1.2.1.43.5.1.1.17.1", new OctetString("SN" + community));
        Pages(pages);
        for (var i = 0; i < supplies.Length; i++)
        {
            var n = i + 1; var s = supplies[i];
            Set($"1.3.6.1.2.1.43.11.1.1.3.1.{n}", new Integer32(s.Color.Length > 0 ? n : 0));
            Set($"1.3.6.1.2.1.43.11.1.1.5.1.{n}", new Integer32(s.Type));
            Set($"1.3.6.1.2.1.43.11.1.1.6.1.{n}", new OctetString(s.Desc));
            Set($"1.3.6.1.2.1.43.11.1.1.8.1.{n}", new Integer32(100));
            Level(n, s.Level);
            if (s.Color.Length > 0) Set($"1.3.6.1.2.1.43.12.1.1.4.1.{n}", new OctetString(s.Color));
        }
        _ = Loop();
    }

    public void Set(string oid, ISnmpData v) => data[oid] = v;
    public void Level(int supply, int level) => Set($"1.3.6.1.2.1.43.11.1.1.9.1.{supply}", new Integer32(level));
    public void Pages(long n) => Set("1.3.6.1.2.1.43.10.2.1.4.1.1", new Counter32((uint)n));

    IEnumerable<(ObjectIdentifier Id, ISnmpData V)> Sorted() =>
        data.Select(kv => (Id: new ObjectIdentifier(kv.Key), V: kv.Value)).OrderBy(x => x.Id);

    async Task Loop()
    {
        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(cts.Token); } catch { return; }
            if (Silent) continue;
            try
            {
                var m = MessageFactory.ParseMessages(r.Buffer, new UserRegistry())[0];
                if (m.Parameters.UserName.ToString() != Community) continue; // yanlış topluluk: yanıt yok (gerçek yazıcılar gibi)
                var vars = new List<Variable>();
                foreach (var v in m.Pdu().Variables)
                {
                    if (m.TypeCode() == SnmpType.GetRequestPdu)
                        vars.Add(data.TryGetValue(v.Id.ToString(), out var val) ? new Variable(v.Id, val) : new Variable(v.Id, new NoSuchObject()));
                    else
                    {
                        var next = Sorted().FirstOrDefault(x => x.Id.CompareTo(v.Id) > 0);
                        vars.Add(next.Id is null ? new Variable(v.Id, new EndOfMibView()) : new Variable(next.Id, next.V));
                    }
                }
                var resp = new ResponseMessage(m.RequestId(), m.Version, m.Parameters.UserName, ErrorCode.NoError, 0, vars);
                await udp.SendAsync(resp.ToBytes(), r.RemoteEndPoint);
            }
            catch (Exception) { /* bozuk paket: yok say */ }
        }
    }

    public void Dispose() { cts.Cancel(); udp.Dispose(); }
}

[Collection("app")]
public class PrinterTests(TestApp app)
{
    Guid Wh(string name) => app.Db(db => db.Warehouses.First(w => w.Name == name).Id);
    static string Community() => "c" + RandomNumberGenerator.GetHexString(8).ToLowerInvariant();

    static FakeSnmpAgent ColorPrinter(string? community = null, string model = "HP Color LaserJet Pro M454dn") => new(community ?? Community(), model, 48213,
        ("Black Cartridge HP W2030A", "black", 3, 8), ("Cyan Cartridge HP W2031A", "cyan", 3, 64),
        ("Magenta Cartridge HP W2033A", "magenta", 3, 0), ("Yellow Cartridge HP W2032A", "yellow", 3, -3), ("Imaging Drum", "", 9, 71));

    async Task<PrinterDetail> Add(HttpClient c, FakeSnmpAgent agent, string warehouse = "Ankara Merkez Depo", string? name = null)
    {
        var r = await c.PostJson("/api/printers", new PrinterSave(Wh(warehouse), name ?? "Yazıcı " + agent.Community, "127.0.0.1", agent.Port, "v2c", agent.Community, "Kat 3", 15, true));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Read<PrinterDetail>();
    }

    [Fact]
    public async Task Probe_reads_printer_mib_over_snmp()
    {
        using var agent = ColorPrinter();
        var admin = await app.Login("yonetici");
        var res = await (await admin.PostJson("/api/printers/probe", new PrinterProbeRequest("127.0.0.1", agent.Port, "v2c", agent.Community, null))).Read<PrinterProbeResult>();
        Assert.True(res.Ok, res.Message);
        Assert.Equal("HP Color LaserJet Pro M454dn", res.Model);
        Assert.Equal(48213, res.PageCount);
        Assert.Equal(["renk_black", "renk_cyan", "renk_magenta", "renk_yellow", "9_imaging_drum"], res.Supplies.Select(s => s.Key));
        var black = res.Supplies[0];
        Assert.Equal((8, "low"), (black.Percent, black.State));
        Assert.Equal(("Var", "ok"), (res.Supplies[3].Level, res.Supplies[3].State));
        Assert.Equal("Drum (OPC)", res.Supplies[4].Type);

        var wrong = await (await admin.PostJson("/api/printers/probe", new PrinterProbeRequest("127.0.0.1", agent.Port, "v2c", "yanlis", null))).Read<PrinterProbeResult>();
        Assert.False(wrong.Ok);
        Assert.Contains("yanıt vermedi", wrong.Message);

        var outside = await (await admin.PostJson("/api/printers/probe", new PrinterProbeRequest("8.8.8.8", 161, "v2c", "public", null))).Read<PrinterProbeResult>();
        Assert.False(outside.Ok);
        Assert.Contains("izin verilen", outside.Message);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJson("/api/printers", new PrinterSave(Wh("Ankara Merkez Depo"), "Dış", "8.8.8.8", 161, "v2c", "public", null, 15, true))).StatusCode);
    }

    [Fact]
    public async Task Low_toner_warns_once_with_mapped_toner_and_stock()
    {
        using var agent = ColorPrinter();
        using var twin = ColorPrinter(model: agent.Community + " model");
        using var twin2 = ColorPrinter(model: agent.Community + " model");
        var admin = await app.Login("yonetici");
        var p = await Add(admin, twin);
        var p2 = await Add(admin, twin2);
        Assert.Equal(2, p.Printer.Warnings.Count); // siyah %8 (uyarı) ve macenta %0 (kritik)
        Assert.Contains(p.Printer.Warnings, w => w.SupplyKey == "renk_magenta" && w.Severity == "Kritik");
        Assert.Contains("tanımlanmamış", p.Printer.Warnings.First(w => w.SupplyKey == "renk_black").Message);
        Assert.Contains(p.Events, e => e.Type == "Sarf bitti");

        // Toner eşleme aynı modeldeki diğer yazıcıya da uygulanır.
        var toner = app.Db(db => db.StockItems.First(s => s.Code == "SRF-TNR-014"));
        var map = await admin.PutJson($"/api/printers/{p.Printer.Id}/supplies/renk_black/toner", new PrinterTonerMap(toner.Id, true));
        Assert.Equal(2, (await map.Read<JsonElement>()).GetProperty("updated").GetInt32());
        var d2 = await admin.GetJson<PrinterDetail>($"/api/printers/{p2.Printer.Id}");
        var w = d2.Printer.Warnings.First(x => x.SupplyKey == "renk_black");
        Assert.Equal(toner.Id, w.StockItemId);
        Assert.Contains("HP 151A toner (SRF-TNR-014)", w.Message);
        Assert.Contains("Ankara Merkez Depo", w.Message);

        // Uyarı e-postası hedef deponun yetkililerine bir kez gider; tekrar okumada yinelenmez.
        int Mails() => app.Db(db => db.OutboxMails.Count(m => m.Kind == "Toner" && m.DedupKey!.StartsWith($"supply-low:{p.Printer.Id}:")));
        var first = Mails();
        Assert.True(first >= 2);
        Assert.Contains("burak.kaya@nurol.com.tr", app.Db(db => db.OutboxMails.Where(m => m.DedupKey!.StartsWith($"supply-low:{p.Printer.Id}:")).Select(m => m.ToEmail).ToList()));
        await admin.PostAsync($"/api/printers/{p.Printer.Id}/refresh", null);
        Assert.Equal(first, Mails());

        // Toner ihtiyacı tablosu: eşlenen toner, kullanan ve düşük seviyedeki yazıcılar.
        var needs = await admin.GetJson<List<TonerNeedRow>>("/api/printers/toner-needs");
        var n = needs.First(x => x.StockItemId == toner.Id);
        Assert.True(n.PrinterCount >= 2 && n.LowCount >= 2);
        Assert.Contains(twin.Community, string.Join(",", n.Printers));
    }

    [Fact]
    public async Task Replacement_is_detected_and_can_be_consumed_from_stock()
    {
        using var agent = ColorPrinter();
        var admin = await app.Login("yonetici");
        var p = await Add(admin, agent);
        var toner = app.Db(db => db.StockItems.First(s => s.Code == "SRF-TNR-014"));
        await admin.PutJson($"/api/printers/{p.Printer.Id}/supplies/renk_black/toner", new PrinterTonerMap(toner.Id, false));
        // Değişimden önce stokta toner olsun.
        await admin.PostJson("/api/stock/movements", new StockMovementRequest(toner.Id, Wh("Ankara Merkez Depo"), StockMovementType.In, 3, null, null, null));
        decimal Bal() => app.Db(db => db.StockBalances.First(b => b.StockItemId == toner.Id && b.WarehouseId == Wh("Ankara Merkez Depo")).Quantity);
        var before = Bal();

        agent.Level(1, 100);
        agent.Pages(48900);
        var d = await (await admin.PostAsync($"/api/printers/{p.Printer.Id}/refresh", null)).Read<PrinterDetail>();
        var black = d.Printer.Supplies.First(s => s.Key == "renk_black");
        Assert.True(black.PendingReplacement);
        Assert.Equal("ok", black.State);
        Assert.DoesNotContain(d.Printer.Warnings, x => x.SupplyKey == "renk_black");
        Assert.Contains(d.Events, e => e.Type == "Değişim algılandı");
        Assert.Equal(48900, d.Printer.PageCount);

        var r = await admin.PostJson($"/api/printers/{p.Printer.Id}/supplies/renk_black/replace", new PrinterReplace(true, null));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var after = await r.Read<PrinterDetail>();
        Assert.False(after.Printer.Supplies.First(s => s.Key == "renk_black").PendingReplacement);
        Assert.Equal(before - 1, Bal());
        Assert.True(app.Db(db => db.StockMovements.Any(m => m.StockItemId == toner.Id && m.Project == "Yazıcı: " + p.Printer.Name)));

        // Eşlenmemiş yuva stoktan düşülemez.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJson($"/api/printers/{p.Printer.Id}/supplies/renk_cyan/replace", new PrinterReplace(true, null))).StatusCode);
    }

    [Fact]
    public async Task Unreachable_printer_keeps_last_levels_and_counter_report_works()
    {
        using var agent = ColorPrinter();
        var admin = await app.Login("yonetici");
        var p = await Add(admin, agent);
        agent.Pages(49013);
        await admin.PostAsync($"/api/printers/{p.Printer.Id}/refresh", null);
        // Dünün kaydı (sayaç farkı rapor için).
        app.Db(db => db.Database.ExecuteSql($"UPDATE printer_counters SET last_count = 48213, first_count = 48000, day = day - 1 WHERE printer_id = {p.Printer.Id}"));
        await admin.PostAsync($"/api/printers/{p.Printer.Id}/refresh", null);
        var rep = await admin.GetJson<ReportTable>("/api/reports/yazici-sayac");
        var row = rep.Rows.First(r => ((JsonElement)r[0]!).GetString() == p.Printer.Name);
        // Aralık dünü de kapsar: dünün ilk sayacı (48000) → bugünkü son sayaç (49013).
        Assert.Equal(1013, ((JsonElement)row[7]!).GetInt64());

        agent.Silent = true;
        var d = await (await admin.PostAsync($"/api/printers/{p.Printer.Id}/refresh", null)).Read<PrinterDetail>();
        Assert.False(d.Printer.Online);
        Assert.NotNull(d.Printer.LastError);
        Assert.Equal(5, d.Printer.Supplies.Count); // son bilinen seviyeler korunur
        Assert.Contains(d.Events, e => e.Type == "Ulaşılamıyor");
    }

    [Fact]
    public async Task Printers_follow_warehouse_scope()
    {
        using var agent = ColorPrinter();
        var admin = await app.Login("yonetici");
        var p = await Add(admin, agent, "İstanbul Şantiye Deposu");
        var burak = await app.Login("burak.kaya");
        Assert.DoesNotContain(await burak.GetJson<List<PrinterListRow>>("/api/printers"), x => x.Id == p.Printer.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await burak.GetAsync($"/api/printers/{p.Printer.Id}")).StatusCode);
        var auditor = await app.Login("zeynep.celik");
        Assert.Contains(await auditor.GetJson<List<PrinterListRow>>("/api/printers"), x => x.Id == p.Printer.Id && !x.CanManage);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PutJson($"/api/printers/{p.Printer.Id}/supplies/renk_black/toner", new PrinterTonerMap(null, false))).StatusCode);
        // Aynı adres iki kez eklenemez.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJson("/api/printers", new PrinterSave(Wh("Ankara Merkez Depo"), "Kopya", "127.0.0.1", agent.Port, "v2c", agent.Community, null, 15, true))).StatusCode);
    }

    [Fact]
    public void Level_interpretation_follows_printer_mib()
    {
        PrinterMonitor.StoredSupply S(int max, int level, int type = 3) => new("k", 1, "Toner", "black", type, max, level);
        Assert.Equal((8, "low"), Pick(PrinterMonitor.Interpret(S(100, 8), 15)));
        Assert.Equal((50, "ok"), Pick(PrinterMonitor.Interpret(S(2000, 1000), 15)));
        Assert.Equal((0, "empty"), Pick(PrinterMonitor.Interpret(S(100, 0), 15)));
        Assert.Equal((null, "ok"), Pick(PrinterMonitor.Interpret(S(100, -3), 15)));       // "biraz var"
        Assert.Equal((null, "unknown"), Pick(PrinterMonitor.Interpret(S(100, -2), 15)));
        Assert.Equal((40, "ok"), Pick(PrinterMonitor.Interpret(S(-2, 40), 15)));         // kapasite bilinmiyor, seviye yüzde
        Assert.True(SnmpPrinterProbe.Allowed(IPAddress.Parse("10.20.4.11"), PrinterOptions.DefaultNetworks));
        Assert.False(SnmpPrinterProbe.Allowed(IPAddress.Parse("8.8.8.8"), PrinterOptions.DefaultNetworks));
        static (int?, string) Pick((int? P, string L, string S) x) => (x.P, x.S);
    }
}

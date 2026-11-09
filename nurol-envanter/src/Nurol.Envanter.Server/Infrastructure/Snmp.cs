using System.Net;
using System.Net.Sockets;
using System.Text;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;

namespace Nurol.Envanter.Server.Infrastructure;

public class PrinterOptions
{
    /// <summary>Arka plan izlemesi (periyodik okuma) açık mı. Kapalıyken yalnızca elle "Şimdi yenile" ile okunur.</summary>
    public bool Enabled { get; set; } = true;
    public int PollMinutes { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 4;
    public int Parallelism { get; set; } = 16;
    /// <summary>
    /// SNMP ile bağlanılabilecek ağlar (CIDR). Uygulamanın rastgele adreslere istek atmasını engeller.
    /// Ayar verilmezse özel ağlar (RFC 1918) kullanılır; verilirse yalnızca verilen liste geçerlidir (daraltılabilir).
    /// </summary>
    public string[] AllowedNetworks { get; set; } = [];
    public static readonly string[] DefaultNetworks = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];
}

/// <summary>Printer-MIB'den (RFC 3805) okunan bir sarf yuvası.</summary>
public record SupplyReading(int Index, string Description, string? Color, int TypeCode, int Max, int Level);
public record PrinterSnapshot(string? Model, string? SysName, string? SerialNumber, long? PageCount, IReadOnlyList<SupplyReading> Supplies);

public class PrinterProbeException(string message, Exception? inner = null) : Exception(message, inner);

public interface IPrinterProbe
{
    Task<PrinterSnapshot> ProbeAsync(string host, int port, string version, string community, CancellationToken ct = default);
}

/// <summary>SNMP v1/v2c ile Printer-MIB okuması (Lextm.SharpSnmpLib).</summary>
public class SnmpPrinterProbe(PrinterOptions o) : IPrinterProbe
{
    const string SysDescr = "1.3.6.1.2.1.1.1.0";
    const string SysName = "1.3.6.1.2.1.1.5.0";
    const string HrDeviceDescr = "1.3.6.1.2.1.25.3.2.1.3.1";
    const string Serial = "1.3.6.1.2.1.43.5.1.1.17.1";
    const string LifeCount = "1.3.6.1.2.1.43.10.2.1.4.1.1";
    const string SuppliesTable = "1.3.6.1.2.1.43.11.1.1";
    const string ColorantValue = "1.3.6.1.2.1.43.12.1.1.4";

    public async Task<PrinterSnapshot> ProbeAsync(string host, int port, string version, string community, CancellationToken ct = default)
    {
        var ip = await Resolve(host, o, ct);
        var ep = new IPEndPoint(ip, port);
        var ver = version == "v1" ? VersionCode.V1 : VersionCode.V2;
        var comm = new OctetString(community);

        // Tek tek alınır: v1'de eksik bir OID tüm isteği bozar.
        async Task<ISnmpData?> Get(string oid)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
            try
            {
                var r = await Messenger.GetAsync(ver, ep, comm, [new Variable(new ObjectIdentifier(oid))], cts.Token);
                var d = r.FirstOrDefault()?.Data;
                return d is null || d.TypeCode is SnmpType.NoSuchObject or SnmpType.NoSuchInstance or SnmpType.EndOfMibView ? null : d;
            }
            catch (ErrorException) { return null; }
        }

        async Task<List<Variable>> Walk(string table)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds * 4));
            var list = new List<Variable>();
            await Messenger.WalkAsync(ver, ep, comm, new ObjectIdentifier(table), list, WalkMode.WithinSubtree, cts.Token);
            return list;
        }

        ISnmpData? descr;
        try { descr = await Get(SysDescr); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PrinterProbeException($"{host}:{port} yanıt vermedi. Yazıcının açık olduğunu, SNMP'nin etkin olduğunu ve topluluk adının doğru olduğunu kontrol edin.");
        }
        catch (SocketException ex)
        {
            throw new PrinterProbeException($"{host}:{port} adresine ulaşılamadı ({ex.SocketErrorCode}).", ex);
        }
        if (descr is null) throw new PrinterProbeException($"{host} SNMP yanıtı verdi ama sistem bilgisi okunamadı.");

        try
        {
            var model = Text(await Get(HrDeviceDescr)) ?? Text(descr);
            var name = Text(await Get(SysName));
            var serial = Text(await Get(Serial));
            var count = Number(await Get(LifeCount));

            var rows = await Walk(SuppliesTable);
            var colorants = (await Walk(ColorantValue)).ToDictionary(v => Last(v.Id), v => Text(v.Data));
            // OID: ...43.11.1.1.{sütun}.{hrDeviceIndex}.{yuva}
            var supplies = rows.Select(v => (Col: Part(v.Id, -3), Idx: Last(v.Id), v.Data))
                .GroupBy(x => x.Idx).Select(g =>
                {
                    ISnmpData? C(int col) => g.FirstOrDefault(x => x.Col == col).Data;
                    var colorIdx = (int)(Number(C(3)) ?? 0);
                    return new SupplyReading(g.Key, Text(C(6)) ?? $"Sarf {g.Key}", colorIdx > 0 ? colorants.GetValueOrDefault(colorIdx) : null,
                        (int)(Number(C(5)) ?? 1), (int)(Number(C(8)) ?? -2), (int)(Number(C(9)) ?? -2));
                }).OrderBy(s => s.Index).ToList();
            return new PrinterSnapshot(Clean(model), Clean(name), Clean(serial), count, supplies);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PrinterProbeException($"{host} bilgilerinin bir kısmı zaman aşımına uğradı. Yazıcı meşgul olabilir; birazdan tekrar deneyin.");
        }
    }

    public static async Task<IPAddress> Resolve(string host, PrinterOptions o, CancellationToken ct)
    {
        IPAddress? ip;
        if (!IPAddress.TryParse(host.Trim(), out ip))
        {
            try { ip = (await Dns.GetHostAddressesAsync(host.Trim(), AddressFamily.InterNetwork, ct)).FirstOrDefault(); }
            catch (SocketException) { ip = null; }
            if (ip is null) throw new PrinterProbeException($"“{host}” adı çözümlenemedi. IP adresi girin veya DNS kaydını kontrol edin.");
        }
        if (!Allowed(ip, o.AllowedNetworks))
            throw new PrinterProbeException($"{ip} izin verilen yazıcı ağlarının dışında ({string.Join(", ", o.AllowedNetworks)}). Gerekirse Printers:AllowedNetworks ayarını genişletin.");
        return ip;
    }

    public static bool Allowed(IPAddress ip, IEnumerable<string> networks)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        foreach (var n in networks)
            if (IPNetwork.TryParse(n, out var net) && net.Contains(ip)) return true;
        return false;
    }

    static int Last(ObjectIdentifier id) => (int)id.ToNumerical()[^1];
    static int Part(ObjectIdentifier id, int fromEnd) { var n = id.ToNumerical(); return (int)n[n.Length + fromEnd]; }

    static long? Number(ISnmpData? d) => d switch
    {
        Integer32 i => i.ToInt32(),
        Counter32 c => (long)c.ToUInt32(),
        Gauge32 g => (long)g.ToUInt32(),
        Counter64 c => (long)c.ToUInt64(),
        _ => null
    };

    static string? Text(ISnmpData? d) => d switch
    {
        null => null,
        OctetString s => DecodeText(s.GetRaw()),
        _ => d.ToString()
    };

    /// <summary>Bazı yazıcılar UTF-8, bazıları Latin-1 gönderir; sondaki NUL baytları atılır.</summary>
    static string DecodeText(byte[] raw)
    {
        var len = raw.Length;
        while (len > 0 && raw[len - 1] == 0) len--;
        try { return new UTF8Encoding(false, true).GetString(raw, 0, len); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(raw, 0, len); }
    }

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

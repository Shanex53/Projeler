using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

/// <summary>
/// Hazır raporlar. Hepsi kullanıcının şirket/depo kapsamıyla süzülür; ?format=xlsx ile Excel olarak iner.
/// Değer raporu yalnızca maliyeti girilmiş kayıtlar üzerinden hesaplanır (satın alma süreci yoktur).
/// </summary>
public static class ReportEndpoints
{
    static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static readonly ReportInfo[] Catalog =
    [
        new("zimmet", "Personel bazlı zimmet", "Kimde hangi cihaz var, ne zamandan beri; imzalı evrak durumu.", false, false, []),
        new("eksik-evrak", "Eksik imzalı evrak", "İmzalı kopyası yüklenmemiş zimmet ve iade tutanakları, bekleme süresiyle.", false, false, []),
        new("sarf", "Sarf tüketimi", "Tarih aralığında malzeme çıkışları; birim, proje veya malzemeye göre özet.", true, true, ["birim", "proje", "malzeme"]),
        new("deger", "Envanter değeri", "Maliyeti girilmiş tekil cihazların depo veya kategori bazında toplamı.", false, true, ["depo", "kategori"]),
        new("ayrilan", "Ayrılan personel", "Pasif durumda olup üzerinde açık zimmet kalan personel.", false, false, []),
        new("hareket", "Hareket dökümü", "Tarih aralığındaki cihaz ve stok hareketleri.", true, false, []),
        new("hatlar", "Telefon hatları ve maliyet", "Hatlar kimde, hangi cihazda; aylık ücret toplamları operatör, birim veya masraf yerine göre.", false, true, ["hat", "operator", "birim", "masraf"]),
        new("yazici-sayac", "Yazıcı sayaçları", "Tarih aralığında yazıcı başına basılan sayfa (SNMP sayaç okumalarından).", true, false, []),
    ];

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/reports", () => Catalog);
        api.MapGet("/reports/{key}", async (string key, HttpContext ctx, AppDbContext db, DateOnly? from, DateOnly? to, string? groupBy, Guid? warehouseId, string? format) =>
        {
            var a = await ctx.Access();
            var (start, end) = Range(from, to);
            var t = key switch
            {
                "zimmet" => await Assigned(db, a, warehouseId),
                "eksik-evrak" => await MissingDocs(db, a, warehouseId),
                "sarf" => await Consumption(db, a, start, end, groupBy ?? "birim", warehouseId),
                "deger" => await Value(db, a, groupBy ?? "depo"),
                "ayrilan" => await Leavers(db, a),
                "hareket" => await Movements(db, a, start, end, warehouseId),
                "yazici-sayac" => await PrinterCounters(db, a, from, to, warehouseId),
                "hatlar" => await Lines(db, a, groupBy ?? "hat"),
                _ => throw AppException.NotFound("Rapor")
            };
            if (format == "xlsx")
            {
                Audit.Add(db, a, "Dışa aktarım", "Rapor", key, $"{t.Title} · {t.Rows.Count} satır");
                await db.SaveChangesAsync();
                return Results.File(Excel.Write(t), Excel.ContentType, Excel.FileName(t.Title));
            }
            return Results.Ok(t);
        });
    }

    static (DateTimeOffset, DateTimeOffset) Range(DateOnly? from, DateOnly? to)
    {
        var today = DateOnly.FromDateTime(TimeZones.ToLocal(DateTimeOffset.UtcNow));
        var f = from ?? today.AddDays(-30);
        var t = to ?? today;
        if (t < f) (f, t) = (t, f);
        if (t.DayNumber - f.DayNumber > 366) throw AppException.Invalid("Tarih aralığı en fazla bir yıl olabilir.");
        DateTimeOffset Utc(DateOnly d) => new(TimeZoneInfo.ConvertTimeToUtc(d.ToDateTime(TimeOnly.MinValue), TimeZones.Istanbul), TimeSpan.Zero);
        return (Utc(f), Utc(t.AddDays(1)));
    }

    static ReportColumn C(string key, string label, string type = "text") => new(key, label, type);

    static async Task<ReportTable> Assigned(AppDbContext db, Access a, Guid? warehouseId)
    {
        var q = a.Items(db).Where(i => i.Status == ItemStatus.Assigned && i.EmployeeId != null);
        if (warehouseId is Guid w) q = q.Where(i => i.WarehouseId == w);
        var rows = await q.OrderBy(i => i.Employee!.FullName).ThenBy(i => i.Tag).Select(i => new
        {
            i.Employee!.FullName, i.Employee.RegistryNo, i.Employee.Department, Company = i.Company!.Name, i.Employee.IsActive,
            i.Tag, Device = i.AssetModel!.Manufacturer!.Name + " " + i.AssetModel.Name, Category = i.Category!.Name, i.Serial,
            Warehouse = i.Warehouse!.Name, No = i.OpenAssignment!.No, Since = i.OpenAssignment.At,
            Signed = db.Documents.Any(d => d.AssignmentId == i.OpenAssignmentId && d.Kind == DocumentKind.Signed)
        }).ToListAsync();
        var now = DateTimeOffset.UtcNow;
        return new ReportTable("Personel bazlı zimmet", "Açık zimmetler; personel adına göre sıralı.",
            [C("p", "Personel"), C("s", "Sicil"), C("b", "Birim"), C("c", "Şirket"), C("t", "Etiket"), C("d", "Cihaz"), C("k", "Kategori"), C("sn", "Seri no"),
             C("w", "Depo"), C("n", "Tutanak"), C("since", "Zimmet tarihi", "date"), C("g", "Gün", "number"), C("e", "İmzalı evrak"), C("a", "Personel durumu")],
            rows.Select(r => new object?[] { r.FullName, r.RegistryNo, r.Department, r.Company, r.Tag, r.Device, r.Category, r.Serial, r.Warehouse, r.No, r.Since,
                (int)(now - r.Since).TotalDays, r.Signed ? "Yüklendi" : "Eksik", r.IsActive ? "Aktif" : "Pasif" }).ToList(),
            [new("Zimmetli cihaz", rows.Count.ToString(Tr)), new("Personel", rows.Select(r => r.FullName + r.RegistryNo).Distinct().Count().ToString(Tr)),
             new("İmzalı evrakı eksik", rows.Count(r => !r.Signed).ToString(Tr))]);
    }

    static async Task<ReportTable> MissingDocs(AppDbContext db, Access a, Guid? warehouseId)
    {
        var q = a.Assignments(db).Where(x => !x.Documents.Any(d => d.Kind == DocumentKind.Signed));
        if (warehouseId is Guid w) q = q.Where(x => x.WarehouseId == w);
        var rows = await q.OrderBy(x => x.At).Select(x => new { x.No, x.Kind, x.At, x.Employee!.FullName, x.Employee.Department, Wh = x.Warehouse!.Name, N = x.Lines.Count, x.UserName }).ToListAsync();
        var now = DateTimeOffset.UtcNow;
        return new ReportTable("Eksik imzalı evrak", "En eski tutanak en üstte.",
            [C("n", "Tutanak"), C("k", "Tür"), C("at", "Tarih", "datetime"), C("g", "Bekleyen gün", "number"), C("p", "Personel"), C("b", "Birim"), C("w", "Depo"), C("c", "Cihaz", "number"), C("u", "İşlemi yapan")],
            rows.Select(r => new object?[] { r.No, r.Kind == AssignmentKind.Issue ? "Zimmet" : "İade", r.At, (int)(now - r.At).TotalDays, r.FullName, r.Department, r.Wh, r.N, r.UserName }).ToList(),
            [new("Eksik tutanak", rows.Count.ToString(Tr)), new("7 günden eski", rows.Count(r => (now - r.At).TotalDays > 7).ToString(Tr))]);
    }

    static async Task<ReportTable> Consumption(AppDbContext db, Access a, DateTimeOffset start, DateTimeOffset end, string groupBy, Guid? warehouseId)
    {
        var q = a.StockMovements(db).Where(m => m.Type == StockMovementType.Out && m.At >= start && m.At < end);
        if (warehouseId is Guid w) q = q.Where(m => m.WarehouseId == w);
        var rows = await q.OrderBy(m => m.At).Select(m => new
        {
            m.At, m.No, Item = m.StockItem!.Name, m.StockItem.Unit, Qty = -m.Quantity, Wh = m.Warehouse!.Name,
            Emp = m.Employee != null ? m.Employee.FullName : null, Dept = m.Employee != null ? m.Employee.Department : null, m.Project, m.Note
        }).ToListAsync();
        string Key(string? project, string item, string? dept) => groupBy switch { "proje" => project ?? "(proje yok)", "malzeme" => item, _ => dept ?? project ?? "(birim yok)" };
        var summary = rows.GroupBy(r => (Key(r.Project, r.Item, r.Dept), r.Unit)).OrderByDescending(g => g.Count())
            .Select(g => new ReportSummary($"{g.Key.Item1}", $"{g.Sum(x => x.Qty).ToString("#,##0.###", Tr)} {g.Key.Unit} ({g.Count()} çıkış)")).Take(15).ToList();
        summary.Insert(0, new("Toplam çıkış", rows.Count.ToString(Tr)));
        return new ReportTable("Sarf tüketimi", $"{TimeZones.Format(start)[..10]} – {TimeZones.Format(end.AddDays(-1))[..10]} · özet: {groupBy}",
            [C("at", "Tarih", "datetime"), C("n", "Hareket no"), C("m", "Malzeme"), C("q", "Miktar", "number"), C("u", "Birim"), C("w", "Depo"), C("p", "Teslim alan"), C("b", "Personel birimi"), C("pr", "Proje"), C("no", "Açıklama")],
            rows.Select(r => new object?[] { r.At, r.No, r.Item, r.Qty, r.Unit, r.Wh, r.Emp, r.Dept, r.Project, r.Note }).ToList(), summary);
    }

    static async Task<ReportTable> Value(AppDbContext db, Access a, string groupBy)
    {
        var items = await a.Items(db).Where(i => i.Status != ItemStatus.Retired)
            .Select(i => new { G = groupBy == "kategori" ? i.Category!.Name : i.Warehouse!.Name, i.Cost, i.Status }).ToListAsync();
        var groups = items.GroupBy(i => i.G).OrderBy(g => g.Key).Select(g => new object?[]
        {
            g.Key, g.Count(), g.Count(x => x.Cost != null), g.Sum(x => x.Cost ?? 0),
            g.Count(x => x.Cost != null) == 0 ? null : Math.Round(g.Where(x => x.Cost != null).Average(x => x.Cost!.Value), 2),
            g.Count(x => x.Status == ItemStatus.Assigned), g.Count(x => x.Status == ItemStatus.InStock)
        }).ToList();
        var withCost = items.Count(i => i.Cost != null);
        return new ReportTable("Envanter değeri", "Kullanım dışı cihazlar hariç. Toplam yalnızca maliyeti girilmiş kayıtları içerir.",
            [C("g", groupBy == "kategori" ? "Kategori" : "Depo"), C("n", "Cihaz", "number"), C("c", "Maliyeti girilen", "number"), C("t", "Toplam maliyet", "money"),
             C("o", "Ortalama", "money"), C("z", "Zimmetli", "number"), C("d", "Depoda", "number")],
            groups,
            [new("Toplam maliyet", items.Sum(i => i.Cost ?? 0).ToString("#,##0.00", Tr) + " ₺"),
             new("Maliyeti girilen", $"{withCost} / {items.Count} cihaz")]);
    }

    static async Task<ReportTable> Leavers(AppDbContext db, Access a)
    {
        var rows = await a.Items(db).Where(i => i.EmployeeId != null && !i.Employee!.IsActive).OrderBy(i => i.Employee!.FullName).ThenBy(i => i.Tag)
            .Select(i => new { i.Employee!.FullName, i.Employee.RegistryNo, i.Employee.Department, Co = i.Company!.Name, Src = i.Employee.Source, i.Tag,
                Device = i.AssetModel!.Manufacturer!.Name + " " + i.AssetModel.Name, i.Serial, Wh = i.Warehouse!.Name, No = i.OpenAssignment!.No, Since = i.OpenAssignment.At })
            .ToListAsync();
        return new ReportTable("Ayrılan personel", "Pasif personelin üzerinde kalan cihazlar. İade sürecini başlatın.",
            [C("p", "Personel"), C("s", "Sicil"), C("b", "Birim"), C("c", "Şirket"), C("k", "Kaynak"), C("t", "Etiket"), C("d", "Cihaz"), C("sn", "Seri no"), C("w", "Depo"), C("n", "Tutanak"), C("since", "Zimmet tarihi", "date")],
            rows.Select(r => new object?[] { r.FullName, r.RegistryNo, r.Department, r.Co, r.Src == EmployeeSource.Directory ? "AD" : "Elle", r.Tag, r.Device, r.Serial, r.Wh, r.No, r.Since }).ToList(),
            [new("Personel", rows.Select(r => r.FullName + r.RegistryNo).Distinct().Count().ToString(Tr)), new("Cihaz", rows.Count.ToString(Tr))]);
    }

    static async Task<ReportTable> Lines(AppDbContext db, Access a, string groupBy)
    {
        var rows = await LineEndpoints.Rows(a, LineEndpoints.Scoped(db, a).AsNoTracking().Where(l => l.Status != LineStatus.Cancelled).OrderBy(l => l.Number));
        var total = rows.Sum(r => r.MonthlyFee ?? 0);
        List<ReportSummary> summary = [new("Hat", rows.Count.ToString(Tr)), new("Aylık toplam", total.ToString("#,##0.00", Tr) + " ₺"),
            new("Boşta", rows.Count(r => r.HolderId is null).ToString(Tr))];
        if (groupBy == "hat")
            return new ReportTable("Telefon hatları", "İptal edilen hatlar hariç.",
                [C("n", "Numara"), C("o", "Operatör"), C("t", "Hat tipi"), C("ta", "Tarife"), C("f", "Aylık ücret", "money"), C("s", "Durum"), C("p", "Kullanan"),
                 C("b", "Birim"), C("d", "Cihaz"), C("i", "ICCID"), C("m", "Masraf yeri"), C("c", "Şirket"), C("e", "Sözleşme bitişi", "date")],
                rows.Select(r => new object?[] { LineLabels.Number(r.Number), r.Operator, r.LineType, r.Tariff, r.MonthlyFee, LineLabels.Status(r.Status), r.HolderName,
                    r.HolderDepartment, r.ItemTag, r.Iccid, r.CostCenter, r.CompanyName, r.ContractEnd }).ToList(), summary);
        Func<PhoneLineRow, string> key = groupBy switch
        {
            "operator" => r => r.Operator,
            "birim" => r => r.HolderDepartment ?? (r.HolderId is null ? "(boşta)" : "(birimi yok)"),
            _ => r => r.CostCenter ?? "(masraf yeri yok)"
        };
        var label = groupBy switch { "operator" => "Operatör", "birim" => "Birim", _ => "Masraf yeri" };
        return new ReportTable($"Hat maliyeti · {label.ToLower(Tr)}", "İptal edilen hatlar hariç; aylık ücreti girilmemiş hatlar toplama katılmaz.",
            [C("g", label), C("n", "Hat", "number"), C("a", "Aktif", "number"), C("s", "Askıda", "number"), C("f", "Aylık toplam", "money"), C("y", "Yıllık tahmini", "money")],
            rows.GroupBy(key).OrderByDescending(g => g.Sum(r => r.MonthlyFee ?? 0)).Select(g => new object?[]
            {
                g.Key, g.Count(), g.Count(r => r.Status == LineStatus.Active), g.Count(r => r.Status == LineStatus.Suspended),
                g.Sum(r => r.MonthlyFee ?? 0), g.Sum(r => r.MonthlyFee ?? 0) * 12
            }).ToList(), summary);
    }

    static async Task<ReportTable> PrinterCounters(AppDbContext db, Access a, DateOnly? from, DateOnly? to, Guid? warehouseId)
    {
        var today = DateOnly.FromDateTime(TimeZones.ToLocal(DateTimeOffset.UtcNow));
        var f = from ?? today.AddDays(-30); var t = to ?? today;
        if (t < f) (f, t) = (t, f);
        var printers = Nurol.Envanter.Server.Services.PrinterMonitor.Scoped(db, a);
        if (warehouseId is Guid w) printers = printers.Where(p => p.WarehouseId == w);
        var list = await printers.OrderBy(p => p.Warehouse!.Name).ThenBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, p.Host, p.Location, p.Model, Wh = p.Warehouse!.Name, p.PageCount }).ToListAsync();
        var ids = list.Select(p => p.Id).ToList();
        var counters = await db.PrinterCounters.Where(c => ids.Contains(c.PrinterId) && c.Day <= t).ToListAsync();
        var rows = list.Select(p =>
        {
            var mine = counters.Where(c => c.PrinterId == p.Id).OrderBy(c => c.Day).ToList();
            // Başlangıç: aralıktan önceki son okuma; yoksa aralıktaki ilk okumanın ilk değeri.
            var startRow = mine.LastOrDefault(c => c.Day < f);
            var inRange = mine.Where(c => c.Day >= f).ToList();
            long? startCount = startRow?.LastCount ?? inRange.FirstOrDefault()?.FirstCount;
            long? endCount = inRange.LastOrDefault()?.LastCount;
            long? pages = startCount is long s && endCount is long e && e >= s ? e - s : null;
            return new object?[] { p.Name, p.Location, p.Model, p.Host, p.Wh, startCount, endCount, pages, p.PageCount };
        }).ToList();
        return new ReportTable("Yazıcı sayaçları", $"{f:dd.MM.yyyy} – {t:dd.MM.yyyy} · sayaç sıfırlanan veya okunamayan yazıcılarda baskı sayısı boş kalır.",
            [C("n", "Yazıcı"), C("l", "Konum"), C("m", "Model"), C("h", "Adres"), C("w", "Depo"), C("s", "Başlangıç sayacı", "number"), C("e", "Bitiş sayacı", "number"),
             C("p", "Basılan sayfa", "number"), C("c", "Güncel sayaç", "number")],
            rows, [new("Yazıcı", list.Count.ToString(Tr)), new("Toplam basılan", rows.Sum(r => (long?)r[7] ?? 0).ToString("#,##0", Tr) + " sayfa")]);
    }

    static async Task<ReportTable> Movements(AppDbContext db, Access a, DateTimeOffset start, DateTimeOffset end, Guid? warehouseId)
    {
        var items = a.Items(db);
        if (warehouseId is Guid w) items = items.Where(i => i.WarehouseId == w);
        var ev = await db.ItemEvents.Where(e => e.At >= start && e.At < end)
            .Join(items, e => e.ItemId, i => i.Id, (e, i) => new { e.At, e.Type, e.Text, e.UserName, e.Reference, i.Tag, Wh = i.Warehouse!.Name })
            .OrderBy(x => x.At).Take(20000).ToListAsync();
        var sm = a.StockMovements(db).Where(m => m.At >= start && m.At < end);
        if (warehouseId is Guid w2) sm = sm.Where(m => m.WarehouseId == w2);
        var st = await sm.OrderBy(m => m.At).Take(20000).Select(m => new { m.At, m.Type, m.No, Item = m.StockItem!.Name, m.Quantity, m.StockItem.Unit, Wh = m.Warehouse!.Name, m.UserName, m.Project, m.Note }).ToListAsync();
        var rows = ev.Select(e => new object?[] { e.At, "Cihaz · " + e.Type, e.Reference ?? e.Tag, e.Tag, e.Text, e.Wh, e.UserName })
            .Concat(st.Select(m => new object?[] { m.At, "Stok · " + Labels.Movement(m.Type), m.No, m.Item, $"{(m.Quantity > 0 ? "+" : "")}{m.Quantity.ToString("#,##0.###", Tr)} {m.Unit}{(m.Project is null ? "" : " · " + m.Project)}{(m.Note is null ? "" : " · " + m.Note)}", m.Wh, m.UserName }))
            .OrderBy(r => (DateTimeOffset)r[0]!).ToList();
        return new ReportTable("Hareket dökümü", $"{TimeZones.Format(start)[..10]} – {TimeZones.Format(end.AddDays(-1))[..10]}",
            [C("at", "Zaman", "datetime"), C("k", "Hareket"), C("r", "Kayıt"), C("o", "Cihaz / malzeme"), C("d", "Ayrıntı"), C("w", "Depo"), C("u", "Yapan")],
            rows, [new("Cihaz hareketi", ev.Count.ToString(Tr)), new("Stok hareketi", st.Count.ToString(Tr))]);
    }
}

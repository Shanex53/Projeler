using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

/// <summary>
/// Telefon hatları ve SIM'ler. Görme: hattın şirketinde okuma yetkisi; değiştirme: şirkette yazma yetkisi.
/// Hat bir cihaza takılı olabilir; cihaz zimmetliyse hattın kullanıcısı cihazın zimmetlisidir.
/// Cihazsız verilen hatlar (ör. kişisel cihazdaki data SIM) doğrudan personele verilir.
/// </summary>
public static class LineEndpoints
{
    static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/lines", async (HttpContext ctx, AppDbContext db, string? q, LineStatus? status, string? op, Guid? companyId, bool? free, Guid? employeeId, Guid? itemId) =>
        {
            var a = await ctx.Access();
            var lines = Scoped(db, a).AsNoTracking();
            if (status is LineStatus s) lines = lines.Where(l => l.Status == s);
            if (!string.IsNullOrWhiteSpace(op)) lines = lines.Where(l => l.Operator == op);
            if (companyId is Guid c) lines = lines.Where(l => l.CompanyId == c);
            if (free == true) lines = lines.Where(l => l.EmployeeId == null && (l.ItemId == null || l.Item!.EmployeeId == null));
            if (employeeId is Guid e) lines = lines.Where(l => l.EmployeeId == e || (l.Item != null && l.Item.EmployeeId == e));
            if (itemId is Guid i) lines = lines.Where(l => l.ItemId == i);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var digits = new string(q.Where(char.IsDigit).ToArray());
                var t = $"%{q.Trim()}%";
                lines = lines.Where(l => (digits.Length >= 3 && (l.Number.Contains(digits) || (l.Iccid != null && l.Iccid.Contains(digits)))) ||
                    EF.Functions.ILike(l.Tariff ?? "", t) || EF.Functions.ILike(l.CostCenter ?? "", t) ||
                    (l.Employee != null && EF.Functions.ILike(l.Employee.FullName, t)) ||
                    (l.Item != null && (EF.Functions.ILike(l.Item.Tag, t) || (l.Item.Employee != null && EF.Functions.ILike(l.Item.Employee.FullName, t)))));
            }
            return await Rows(a, lines.OrderBy(l => l.Number));
        });

        api.MapGet("/lines/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) => await Detail(db, await ctx.Access(), id));

        api.MapPost("/lines", async (PhoneLineSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            if (!a.CanWriteCompany(req.CompanyId)) throw AppException.Forbidden("Bu şirkete hat ekleme yetkiniz yok.");
            var line = new PhoneLine { Id = Guid.NewGuid(), CompanyId = req.CompanyId, CreatedAt = DateTimeOffset.UtcNow };
            await Apply(db, line, req, a.UserName);
            db.PhoneLines.Add(line);
            Event(db, line.Id, "Kayıt", $"{LineLabels.Number(line.Number)} · {line.Operator} · {line.LineType}", a.UserName);
            Audit.Add(db, a, "Oluşturma", "Hat", line.Id, line.Number);
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, a, line.Id));
        });

        api.MapPut("/lines/{id:guid}", async (Guid id, PhoneLineSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var line = await Scoped(db, a).FirstOrDefaultAsync(l => l.Id == id) ?? throw AppException.NotFound("Hat");
            if (!a.CanWriteCompany(line.CompanyId)) throw AppException.Forbidden();
            if (req.CompanyId != line.CompanyId) throw AppException.Conflict("Hat başka şirkete taşınamaz.");
            if (req.Version is uint v && v != line.Version) throw AppException.Conflict("Hat siz düzenlerken başka biri tarafından değiştirildi. Sayfayı yenileyip tekrar deneyin.");
            if (req.Version is uint ov) db.Entry(line).Property(x => x.Version).OriginalValue = ov;
            var before = (line.Status, line.Tariff, line.MonthlyFee, line.Iccid, line.Operator);
            await Apply(db, line, req, a.UserName);
            var changes = new List<string>();
            if (before.Status != line.Status) changes.Add($"Durum: {LineLabels.Status(before.Status)} → {LineLabels.Status(line.Status)}");
            if (before.Iccid != line.Iccid) changes.Add($"SIM değişti ({before.Iccid ?? "—"} → {line.Iccid ?? "—"})");
            if (before.Tariff != line.Tariff || before.MonthlyFee != line.MonthlyFee) changes.Add($"Tarife: {line.Tariff ?? "—"} · {line.MonthlyFee?.ToString("#,##0.00", Tr) ?? "—"} ₺");
            if (before.Operator != line.Operator) changes.Add($"Operatör: {before.Operator} → {line.Operator}");
            Event(db, line.Id, line.Status == LineStatus.Cancelled && before.Status != LineStatus.Cancelled ? "İptal" : "Düzenleme",
                changes.Count > 0 ? string.Join(" · ", changes) : "Bilgiler güncellendi", a.UserName);
            Audit.Add(db, a, "Güncelleme", "Hat", line.Id, line.Number);
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, a, id));
        });

        api.MapPost("/lines/{id:guid}/assign", async (Guid id, LineAssign req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var line = await Scoped(db, a).Include(l => l.Employee).FirstOrDefaultAsync(l => l.Id == id) ?? throw AppException.NotFound("Hat");
            if (!a.CanWriteCompany(line.CompanyId)) throw AppException.Forbidden();
            if (req.EmployeeId is Guid eid)
            {
                if (line.Status == LineStatus.Cancelled) throw AppException.Conflict("İptal edilmiş hat personele verilemez.");
                var emp = await a.Employees(db).FirstOrDefaultAsync(e => e.Id == eid) ?? throw AppException.NotFound("Personel");
                if (emp.CompanyId != line.CompanyId) throw AppException.Conflict("Hat yalnızca aynı şirketin personeline verilebilir.");
                if (!emp.IsActive) throw AppException.Conflict("Pasif personele hat verilemez.");
                var prev = line.Employee?.FullName;
                if (prev is not null && line.EmployeeId != emp.Id) TakeBack(db, line, prev, "devredildi", a.UserName);
                line.EmployeeId = emp.Id;
                Event(db, line.Id, "Personele verildi", $"{emp.FullName}{(prev is null ? "" : $" (önceki: {prev})")}{Note(req.Note)}", a.UserName, emp.Id);
            }
            else
            {
                if (line.EmployeeId is null) throw AppException.Conflict("Hat zaten bir personelde değil.");
                TakeBack(db, line, line.Employee!.FullName, req.Note, a.UserName);
            }
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, a, id));
        });

        api.MapPost("/lines/{id:guid}/install", async (Guid id, LineInstall req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var line = await Scoped(db, a).Include(l => l.Item).FirstOrDefaultAsync(l => l.Id == id) ?? throw AppException.NotFound("Hat");
            if (!a.CanWriteCompany(line.CompanyId)) throw AppException.Forbidden();
            if (req.ItemId is Guid iid)
            {
                var item = await a.Items(db).FirstOrDefaultAsync(i => i.Id == iid) ?? throw AppException.NotFound("Cihaz");
                if (item.CompanyId != line.CompanyId) throw AppException.Conflict("Hat yalnızca aynı şirketin cihazına takılabilir.");
                if (item.Status == ItemStatus.Retired) throw AppException.Conflict("Kullanım dışı cihaza hat takılamaz.");
                if (await db.PhoneLines.CountAsync(l => l.ItemId == iid && l.Id != id) >= 2) throw AppException.Conflict($"{item.Tag} cihazında zaten iki hat takılı.");
                var prev = line.Item?.Tag;
                line.ItemId = item.Id;
                Event(db, line.Id, "Cihaza takıldı", $"{item.Tag}{(prev is null ? "" : $" (önceki: {prev})")}{Note(req.Note)}", a.UserName);
                ItemEndpoints.Event(db, item.Id, "Hat", $"{LineLabels.Number(line.Number)} hattı takıldı", a.UserName);
            }
            else
            {
                if (line.ItemId is null) throw AppException.Conflict("Hat bir cihaza takılı değil.");
                Event(db, line.Id, "Cihazdan çıkarıldı", $"{line.Item!.Tag}{Note(req.Note)}", a.UserName);
                ItemEndpoints.Event(db, line.ItemId.Value, "Hat", $"{LineLabels.Number(line.Number)} hattı çıkarıldı", a.UserName);
                line.ItemId = null;
            }
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, a, id));
        });

        // ---- Excel ile toplu hat ekleme ----
        api.MapGet("/import/lines/template", async (HttpContext ctx) =>
        {
            (await ctx.Access()).RequireWrite();
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Hatlar");
            var lists = wb.Worksheets.Add("Listeler");
            string[] h = ["Numara *", "Operatör *", "Hat tipi *", "Tarife", "Aylık ücret (₺)", "ICCID", "Durum", "Masraf yeri", "Sözleşme bitişi", "Personel sicil no", "Cihaz etiketi", "Not"];
            for (var i = 0; i < h.Length; i++)
            {
                var c = ws.Cell(1, i + 1); c.Value = h[i]; c.Style.Font.Bold = true; c.Style.Fill.BackgroundColor = XLColor.FromHtml("#E6EEFC");
                ws.Column(i + 1).Style.NumberFormat.Format = "@"; ws.Column(i + 1).Width = 20;
            }
            ws.Column(5).Style.NumberFormat.Format = "#,##0.00";
            ws.Column(9).Style.DateFormat.Format = "dd.MM.yyyy";
            void L(int col, string title, string[] values, int target)
            {
                lists.Cell(1, col).Value = title;
                for (var i = 0; i < values.Length; i++) lists.Cell(i + 2, col).Value = values[i];
                ws.Range(2, target, 2001, target).CreateDataValidation().List(lists.Range(2, col, values.Length + 1, col), true);
            }
            L(1, "Operatör", LineLabels.Operators, 2);
            L(2, "Hat tipi", LineLabels.Types, 3);
            L(3, "Durum", ["Aktif", "Askıda", "İptal"], 7);
            ws.SheetView.FreezeRows(1);
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return Results.File(ms.ToArray(), Excel.ContentType, "hat-sablonu.xlsx");
        });

        api.MapPost("/import/lines", async (Guid companyId, bool? commit, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            if (!a.CanWriteCompany(companyId)) throw AppException.Forbidden("Bu şirkete hat ekleme yetkiniz yok.");
            if (!ctx.Request.HasFormContentType) throw AppException.Invalid("Excel dosyası seçin.");
            var file = (await ctx.Request.ReadFormAsync()).Files.FirstOrDefault() ?? throw AppException.Invalid("Excel dosyası seçin.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms); ms.Position = 0;
            XLWorkbook wb;
            try { wb = new XLWorkbook(ms); } catch (Exception ex) when (ex is not AppException) { throw AppException.Invalid("Dosya okunamadı. Şablondan oluşturulmuş bir .xlsx yükleyin."); }
            using var _ = wb;
            var ws = wb.Worksheets.First();
            var headers = new Dictionary<string, int>();
            var last = ws.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (var c = 1; c <= last; c++) { var t = ws.Cell(1, c).GetString().Replace("*", "").Trim().ToLower(Tr); if (t.Length > 0) headers[t] = c; }
            string? V(int r, string h) => headers.TryGetValue(h.ToLower(Tr), out var c) ? Cell(ws.Cell(r, c)) : null;
            var errors = new List<ImportError>();
            foreach (var req in new[] { "Numara", "Operatör", "Hat tipi" })
                if (!headers.ContainsKey(req.ToLower(Tr))) errors.Add(new(1, req, $"“{req}” sütunu eksik. Güncel şablonu indirin."));
            if (errors.Count > 0) return Results.Ok(new ImportResult(0, 0, false, errors, [], [], []));

            var employees = await db.Employees.Where(e => e.CompanyId == companyId && e.RegistryNo != null).Select(e => new { e.Id, e.RegistryNo, e.FullName, e.IsActive }).ToListAsync();
            var existingNumbers = (await db.PhoneLines.Select(l => l.Number).ToListAsync()).ToHashSet();
            var existingIccid = (await db.PhoneLines.Where(l => l.Iccid != null).Select(l => l.Iccid!).ToListAsync()).ToHashSet();
            var seenN = new Dictionary<string, int>(); var seenI = new Dictionary<string, int>();
            var rows = new List<PhoneLine>();
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            if (lastRow > 2001) throw AppException.Invalid("Tek seferde en fazla 2000 satır yüklenebilir.");
            for (var r = 2; r <= lastRow; r++)
            {
                if (headers.Values.All(c => string.IsNullOrWhiteSpace(Cell(ws.Cell(r, c))))) continue;
                var before = errors.Count;
                void E(string col, string m) => errors.Add(new ImportError(r, col, m));
                var number = NormalizeNumber(V(r, "Numara"));
                if (number is null) E("Numara", $"“{V(r, "Numara")}” geçerli bir hat numarası değil (ör. 0532 418 27 60).");
                else if (existingNumbers.Contains(number)) E("Numara", $"{LineLabels.Number(number)} zaten kayıtlı.");
                else if (seenN.TryGetValue(number, out var o)) E("Numara", $"{LineLabels.Number(number)} dosyada {o}. satırda da var.");
                else seenN[number] = r;
                var op = LineLabels.Operators.FirstOrDefault(x => string.Equals(x, V(r, "Operatör"), StringComparison.CurrentCultureIgnoreCase));
                if (op is null) E("Operatör", $"Operatör şunlardan biri olmalı: {string.Join(", ", LineLabels.Operators)}.");
                var type = LineLabels.Types.FirstOrDefault(x => string.Equals(x, V(r, "Hat tipi"), StringComparison.CurrentCultureIgnoreCase));
                if (type is null) E("Hat tipi", $"Hat tipi şunlardan biri olmalı: {string.Join(", ", LineLabels.Types)}.");
                var iccid = NormalizeIccid(V(r, "ICCID"), out var iccidErr);
                if (iccidErr is not null) E("ICCID", iccidErr);
                else if (iccid is not null && existingIccid.Contains(iccid)) E("ICCID", $"{iccid} başka bir hatta kayıtlı.");
                else if (iccid is not null && seenI.TryGetValue(iccid, out var oi)) E("ICCID", $"{iccid} dosyada {oi}. satırda da var.");
                else if (iccid is not null) seenI[iccid] = r;
                decimal? fee = null;
                if (V(r, "Aylık ücret (₺)") is { Length: > 0 } feeText)
                {
                    // "250,50" Türkçe, "250.5" Excel sayı hücresi biçimidir.
                    if (decimal.TryParse(feeText.Replace("₺", "").Trim(), NumberStyles.Number, feeText.Contains(',') ? Tr : CultureInfo.InvariantCulture, out var f) && f >= 0) fee = f;
                    else E("Aylık ücret (₺)", $"“{feeText}” geçerli bir tutar değil.");
                }
                var status = (V(r, "Durum") ?? "Aktif").ToLower(Tr) switch { "aktif" => (LineStatus?)LineStatus.Active, "askıda" => LineStatus.Suspended, "iptal" => LineStatus.Cancelled, _ => null };
                if (status is null) E("Durum", "Durum Aktif, Askıda veya İptal olmalı.");
                DateOnly? end = null;
                if (V(r, "Sözleşme bitişi") is { Length: > 0 } endText)
                {
                    if (DateOnly.TryParseExact(endText, ["yyyy-MM-dd", "dd.MM.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) end = d;
                    else E("Sözleşme bitişi", $"“{endText}” geçerli bir tarih değil (gg.aa.yyyy).");
                }
                Guid? empId = null;
                if (V(r, "Personel sicil no") is { Length: > 0 } reg)
                {
                    var emp = employees.FirstOrDefault(e => e.RegistryNo == reg);
                    if (emp is null) E("Personel sicil no", $"{reg} sicil numaralı personel bu şirkette yok.");
                    else if (!emp.IsActive) E("Personel sicil no", $"{emp.FullName} pasif personel.");
                    else empId = emp.Id;
                }
                Guid? itemId = null;
                if (V(r, "Cihaz etiketi") is { Length: > 0 } tag)
                {
                    var item = await a.Items(db).Where(i => i.Tag == tag.ToUpperInvariant()).Select(i => new { i.Id, i.CompanyId }).FirstOrDefaultAsync();
                    if (item is null || item.CompanyId != companyId) E("Cihaz etiketi", $"{tag} bu şirkette bulunamadı veya erişim yetkiniz yok.");
                    else itemId = item.Id;
                }
                if (errors.Count == before)
                    rows.Add(new PhoneLine { Id = Guid.NewGuid(), CompanyId = companyId, Number = number!, Operator = op!, LineType = type!, Tariff = Blank(V(r, "Tarife")),
                        MonthlyFee = fee, Iccid = iccid, Status = status!.Value, CostCenter = Blank(V(r, "Masraf yeri")), ContractEnd = end, EmployeeId = empId, ItemId = itemId,
                        Notes = Blank(V(r, "Not")), CreatedAt = DateTimeOffset.UtcNow });
                if (errors.Count > 500) break;
            }
            var total = rows.Count + errors.Select(e => e.Row).Distinct().Count();
            string[] ph = ["Numara", "Operatör", "Hat tipi", "Tarife", "ICCID"];
            var preview = rows.Take(20).Select(l => new string?[] { LineLabels.Number(l.Number), l.Operator, l.LineType, l.Tariff, l.Iccid }).ToList();
            if (commit != true || errors.Count > 0 || rows.Count == 0) return Results.Ok(new ImportResult(total, rows.Count, false, errors, [], ph, preview));
            db.PhoneLines.AddRange(rows);
            foreach (var l in rows) Event(db, l.Id, "Kayıt", "Excel ile eklendi", a.UserName);
            Audit.Add(db, a, "Excel ile ekleme", "Hat", null, $"{rows.Count} hat");
            await db.SaveChangesAsync();
            return Results.Ok(new ImportResult(total, rows.Count, true, [], rows.Select(l => LineLabels.Number(l.Number)).ToList(), ph, preview));
        }).DisableAntiforgery();
    }

    internal static IQueryable<PhoneLine> Scoped(AppDbContext db, Access a) =>
        a.IsSystemAdmin ? db.PhoneLines : db.PhoneLines.Where(l => a.Companies.Contains(l.CompanyId));

    internal static async Task<List<PhoneLineRow>> Rows(Access a, IQueryable<PhoneLine> q)
    {
        var soon = DateOnly.FromDateTime(TimeZones.ToLocal(DateTimeOffset.UtcNow)).AddDays(30);
        var rows = await q.Select(l => new
        {
            l.Id, l.CompanyId, Co = l.Company!.Name, l.Number, l.Operator, l.LineType, l.Tariff, l.MonthlyFee, l.Iccid, l.Status, l.ItemId,
            Tag = l.Item != null ? l.Item.Tag : null, Model = l.Item != null ? l.Item.AssetModel!.Manufacturer!.Name + " " + l.Item.AssetModel.Name : null,
            l.EmployeeId, Emp = l.Employee != null ? l.Employee.FullName : null, EmpDept = l.Employee != null ? l.Employee.Department : null,
            ItemEmpId = l.Item != null ? l.Item.EmployeeId : null, ItemEmp = l.Item != null && l.Item.Employee != null ? l.Item.Employee.FullName : null,
            ItemEmpDept = l.Item != null && l.Item.Employee != null ? l.Item.Employee.Department : null, l.CostCenter, l.ContractEnd
        }).ToListAsync();
        return rows.Select(l => new PhoneLineRow(l.Id, l.CompanyId, l.Co, l.Number, l.Operator, l.LineType, l.Tariff, l.MonthlyFee, l.Iccid, l.Status, l.ItemId, l.Tag, l.Model,
            l.EmployeeId, l.Emp, l.EmployeeId ?? l.ItemEmpId, l.Emp ?? l.ItemEmp, l.EmployeeId is null ? l.ItemEmpDept : l.EmpDept, l.CostCenter, l.ContractEnd,
            l.ContractEnd is DateOnly d && d <= soon && l.Status != LineStatus.Cancelled, a.CanWriteCompany(l.CompanyId))).ToList();
    }

    static async Task<PhoneLineDetail> Detail(AppDbContext db, Access a, Guid id)
    {
        var row = (await Rows(a, Scoped(db, a).AsNoTracking().Where(l => l.Id == id))).FirstOrDefault() ?? throw AppException.NotFound("Hat");
        var l = await db.PhoneLines.AsNoTracking().FirstAsync(x => x.Id == id);
        var events = await db.PhoneLineEvents.Where(e => e.LineId == id).OrderByDescending(e => e.At).Take(100)
            .Select(e => new PhoneLineEventDto(e.At, e.Type, e.Text, e.UserName)).ToListAsync();
        return new PhoneLineDetail(row, l.Notes, l.Version, events);
    }

    static async Task Apply(AppDbContext db, PhoneLine l, PhoneLineSave r, string user)
    {
        var number = NormalizeNumber(r.Number) ?? throw AppException.Invalid("Geçerli bir hat numarası girin (ör. 0532 418 27 60).", new() { ["number"] = "Geçerli bir numara girin." });
        if (await db.PhoneLines.AnyAsync(x => x.Number == number && x.Id != l.Id)) throw AppException.Conflict($"{LineLabels.Number(number)} numaralı hat zaten kayıtlı.");
        var iccid = NormalizeIccid(r.Iccid, out var err);
        if (err is not null) throw AppException.Invalid(err, new() { ["iccid"] = err });
        if (iccid is not null && await db.PhoneLines.AnyAsync(x => x.Iccid == iccid && x.Id != l.Id)) throw AppException.Conflict($"{iccid} SIM numarası başka bir hatta kayıtlı.");
        if (!LineLabels.Operators.Contains(r.Operator)) throw AppException.Invalid("Operatör seçin.");
        if (!LineLabels.Types.Contains(r.LineType)) throw AppException.Invalid("Hat tipini seçin.");
        if (r.MonthlyFee is < 0) throw AppException.Invalid("Aylık ücret negatif olamaz.");
        l.Number = number; l.Iccid = iccid; l.Operator = r.Operator; l.LineType = r.LineType; l.Tariff = Blank(r.Tariff); l.MonthlyFee = r.MonthlyFee;
        l.Status = r.Status; l.CostCenter = Blank(r.CostCenter); l.ContractEnd = r.ContractEnd; l.Notes = Blank(r.Notes);
        // İptal edilen hat kimsede kalmaz.
        if (l.Status == LineStatus.Cancelled && l.EmployeeId is Guid holder)
            Event(db, l.Id, "Geri alındı", "Hat iptal edildiği için personelden düşüldü", user, holder);
        if (l.Status == LineStatus.Cancelled) l.EmployeeId = null;
    }

    /// <summary>Türkiye cep numaralarını 0 ile başlayan 11 haneye getirir (+90, 90, başta 0 olmadan). M2M/özel numaralar 10-13 hane olarak kabul edilir.</summary>
    public static string? NormalizeNumber(string? raw)
    {
        var d = new string((raw ?? "").Where(char.IsDigit).ToArray());
        if (d.Length == 12 && d.StartsWith("90")) d = "0" + d[2..];
        else if (d.Length == 10 && d[0] == '5') d = "0" + d;
        return d.Length is >= 10 and <= 13 ? d : null;
    }

    public static string? NormalizeIccid(string? raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var d = new string(raw.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant().TrimEnd('F');
        if (d.Length is < 18 or > 20 || !d.All(char.IsAsciiDigit) || !d.StartsWith("89")) { error = "ICCID 89 ile başlayan 18-20 haneli SIM numarası olmalıdır."; return null; }
        return d;
    }

    internal static void Event(AppDbContext db, Guid lineId, string type, string text, string user, Guid? employeeId = null) =>
        db.PhoneLineEvents.Add(new PhoneLineEvent { Id = Guid.NewGuid(), LineId = lineId, At = DateTimeOffset.UtcNow, Type = type, Text = text, UserName = user, EmployeeId = employeeId });

    /// <summary>Hattı doğrudan personelden geri alır (ilişik kesmede de kullanılır).</summary>
    internal static void TakeBack(AppDbContext db, PhoneLine line, string employeeName, string? note, string user)
    {
        Event(db, line.Id, "Geri alındı", $"{employeeName} kişisinden geri alındı{Note(note)}", user, line.EmployeeId);
        line.EmployeeId = null;
    }

    static string Note(string? n) => string.IsNullOrWhiteSpace(n) ? "" : $" · {n.Trim()}";
    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static string? Cell(IXLCell c)
    {
        if (c.IsEmpty()) return null;
        var v = c.Value;
        if (v.IsDateTime) return v.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (v.IsNumber) return v.GetNumber().ToString("0.############", CultureInfo.InvariantCulture);
        var s = c.GetString().Trim();
        return s.Length == 0 ? null : s;
    }
}

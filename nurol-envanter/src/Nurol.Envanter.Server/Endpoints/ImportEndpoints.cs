using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Server.Services;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

/// <summary>
/// Excel ile toplu ekleme. Akış: şablonu indir → doldur → yükle (önizleme, satır bazlı hatalar) → hata yoksa onayla.
/// Onayda tüm satırlar tek transaction'da kaydedilir; bir satır bile kaydedilemezse hiçbiri kaydedilmez.
/// </summary>
public static class ImportEndpoints
{
    const int MaxRows = 2000;
    static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    const string HModel = "Model", HWarehouse = "Depo", HLocation = "Lokasyon", HSerial = "Seri no", HCost = "Maliyet (₺)", HNotes = "Not";
    const string HName = "Ad soyad", HRegistry = "Sicil no", HTitle = "Unvan", HDept = "Birim", HEmail = "E-posta";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/import/items/template", async (Guid categoryId, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireWrite();
            var cat = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId && !c.Archived) ?? throw AppException.NotFound("Kategori");
            var models = await db.AssetModels.Where(m => m.CategoryId == categoryId && !m.Archived).Include(m => m.Manufacturer).OrderBy(m => m.Manufacturer!.Name).ThenBy(m => m.Name).ToListAsync();
            var fields = await ItemFields(db, categoryId, models.Select(m => m.Id).ToList());
            var whs = await Writable(db, a);

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Envanter");
            var lists = wb.Worksheets.Add("Listeler");
            var headers = new List<string> { HModel, HWarehouse, HLocation, HSerial + (cat.RequiresSerial ? " *" : ""), HCost, HNotes };
            headers.AddRange(fields.Select(f => Header(f, models)));
            for (var i = 0; i < headers.Count; i++)
            {
                var c = ws.Cell(1, i + 1);
                c.Value = headers[i];
                c.Style.Font.Bold = true;
                c.Style.Fill.BackgroundColor = XLColor.FromHtml(i < 6 ? "#E6EEFC" : "#ECE9FB");
                // Seri no ve metin alanları metin olarak tutulur; baştaki sıfırlar kaybolmaz.
                ws.Column(i + 1).Style.NumberFormat.Format = "@";
            }
            ws.Column(5).Style.NumberFormat.Format = "#,##0.00";
            for (var i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                if (f.Type == FieldType.Number) ws.Column(7 + i).Style.NumberFormat.Format = "General";
                if (f.Type == FieldType.Date) ws.Column(7 + i).Style.DateFormat.Format = "dd.MM.yyyy";
            }
            ws.SheetView.FreezeRows(1);

            // Açılır listeler: model, depo, seçim alanları.
            void List(int col, string title, IEnumerable<string> values, int targetCol)
            {
                var v = values.ToList();
                lists.Cell(1, col).Value = title;
                for (var i = 0; i < v.Count; i++) lists.Cell(i + 2, col).Value = v[i];
                if (v.Count > 0) ws.Range(2, targetCol, MaxRows + 1, targetCol).CreateDataValidation().List(lists.Range(2, col, v.Count + 1, col), true);
            }
            List(1, "Modeller", models.Select(m => $"{m.Manufacturer!.Name} {m.Name}"), 1);
            List(2, "Depolar", whs.Select(w => w.Name), 2);
            var lc = 3;
            for (var i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                if (f.Type == FieldType.Select) List(lc++, f.Label, f.Options, 7 + i);
                if (f.Type == FieldType.Boolean) List(lc++, f.Label, ["Evet", "Hayır"], 7 + i);
            }
            var locRow = 2;
            lists.Cell(1, lc).Value = "Lokasyonlar (Depo / Lokasyon)";
            foreach (var w in whs) foreach (var l in w.Locations.Where(l => !l.Archived)) lists.Cell(locRow++, lc).Value = $"{w.Name} / {l.Name}";
            lists.Columns().AdjustToContents();

            var help = wb.Worksheets.Add("Açıklama");
            string[] lines =
            [
                $"Kategori: {cat.Name} ({cat.Code}). Etiketler kayıt sırasında otomatik verilir; bu dosyada etiket sütunu yoktur.",
                "Her satır bir cihazdır. Boş satırlar atlanır. En fazla 2000 satır.",
                "Model ve Depo zorunludur; değerleri açılır listeden seçin (Listeler sayfası). Yalnızca yetkili olduğunuz depolar listelenir.",
                "Lokasyon isteğe bağlıdır; seçilen depoya ait olmalıdır.",
                cat.RequiresSerial ? "Seri no bu kategoride zorunludur ve aynı markada benzersiz olmalıdır." : "Seri no isteğe bağlıdır.",
                "Mor başlıklı sütunlar özel alanlardır. Parantez içinde model adı olan alanlar yalnızca o modele aittir.",
                "Yükleme önce önizlenir; hata varsa hiçbir satır kaydedilmez. Hataları düzeltip dosyayı yeniden yükleyin."
            ];
            for (var i = 0; i < lines.Length; i++) help.Cell(i + 1, 1).Value = lines[i];
            help.Column(1).Width = 120;

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return Results.File(ms.ToArray(), Excel.ContentType, $"envanter-sablonu-{cat.Code.ToLowerInvariant()}.xlsx");
        });

        api.MapPost("/import/items", async (Guid categoryId, bool? commit, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireWrite();
            var cat = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId && !c.Archived) ?? throw AppException.NotFound("Kategori");
            using var wb = await ReadWorkbook(ctx);
            var ws = wb.Worksheets.First();
            var models = await db.AssetModels.Where(m => m.CategoryId == categoryId && !m.Archived).Include(m => m.Manufacturer).Include(m => m.Category).ToListAsync();
            var fields = await ItemFields(db, categoryId, models.Select(m => m.Id).ToList());
            var whs = await Writable(db, a);
            var fieldByHeader = fields.ToDictionary(f => Norm(Header(f, models)), f => f);

            var errors = new List<ImportError>();
            var cols = Headers(ws, errors, [HModel, HWarehouse, HLocation, HSerial, HCost, HNotes], fieldByHeader.Keys, [HModel, HWarehouse]);
            if (errors.Count > 0) return Results.Ok(new ImportResult(0, 0, false, errors, [], [], []));

            var rows = new List<(int Row, AssetModel Model, Warehouse Wh, Guid? Loc, string? Serial, decimal? Cost, string? Notes, Dictionary<Guid, string?> Values)>();
            var serials = new Dictionary<(Guid, string), int>();
            var uniques = new Dictionary<(Guid, string), int>();
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            if (lastRow - 1 > MaxRows) throw AppException.Invalid($"Dosyada {lastRow - 1} satır var; tek seferde en fazla {MaxRows} satır yüklenebilir.");

            for (var r = 2; r <= lastRow; r++)
            {
                string? V(string h) => cols.TryGetValue(Norm(h), out var c) ? Read(ws.Cell(r, c)) : null;
                if (cols.Values.All(c => string.IsNullOrWhiteSpace(Read(ws.Cell(r, c))))) continue;
                var before = errors.Count;
                void Err(string? col, string msg) => errors.Add(new ImportError(r, col, msg));

                var modelText = V(HModel);
                var model = models.FirstOrDefault(m => Norm($"{m.Manufacturer!.Name} {m.Name}") == Norm(modelText));
                if (string.IsNullOrWhiteSpace(modelText)) Err(HModel, "Model zorunludur.");
                else if (model is null) Err(HModel, $"“{modelText}” bu kategoride tanımlı bir model değil.");

                var whText = V(HWarehouse);
                var wh = whs.FirstOrDefault(w => Norm(w.Name) == Norm(whText));
                if (string.IsNullOrWhiteSpace(whText)) Err(HWarehouse, "Depo zorunludur.");
                else if (wh is null) Err(HWarehouse, $"“{whText}” deposu bulunamadı veya bu depoda yetkiniz yok.");

                Guid? loc = null;
                var locText = V(HLocation);
                if (!string.IsNullOrWhiteSpace(locText) && wh is not null)
                {
                    var name = locText.Contains('/') ? locText[(locText.LastIndexOf('/') + 1)..] : locText;
                    loc = wh.Locations.FirstOrDefault(l => !l.Archived && Norm(l.Name) == Norm(name))?.Id;
                    if (loc is null) Err(HLocation, $"“{locText}” lokasyonu {wh.Name} deposunda yok.");
                }

                var serial = ItemEndpoints.NormalizeSerial(V(HSerial));
                if (cat.RequiresSerial && serial is null) Err(HSerial, "Seri no bu kategoride zorunludur.");
                if (serial is not null && model is not null)
                {
                    if (serials.TryGetValue((model.ManufacturerId, serial), out var other)) Err(HSerial, $"{serial} seri numarası dosyada {other}. satırda da var.");
                    else serials[(model.ManufacturerId, serial)] = r;
                    var tag = await db.Items.Where(i => i.ManufacturerId == model.ManufacturerId && i.Serial == serial).Select(i => i.Tag).FirstOrDefaultAsync();
                    if (tag is not null) Err(HSerial, $"{serial} seri numarası {tag} kaydında kullanılıyor.");
                }

                decimal? cost = null;
                var costText = V(HCost);
                if (!string.IsNullOrWhiteSpace(costText))
                {
                    if (decimal.TryParse(costText, NumberStyles.Number, CultureInfo.InvariantCulture, out var cv) || decimal.TryParse(costText, NumberStyles.Number, Tr, out cv))
                    { if (cv < 0) Err(HCost, "Maliyet negatif olamaz."); else cost = cv; }
                    else Err(HCost, $"“{costText}” geçerli bir tutar değil.");
                }

                var values = new Dictionary<Guid, string?>();
                foreach (var (header, f) in fieldByHeader)
                {
                    var raw = cols.TryGetValue(header, out var c) ? Read(ws.Cell(r, c), f.Type) : null;
                    var applies = f.CategoryId == categoryId || (model is not null && f.AssetModelId == model.Id);
                    if (!applies)
                    {
                        if (!string.IsNullOrWhiteSpace(raw)) Err(f.Label, $"{f.Label} alanı yalnızca kendi modeline aittir; bu satırın modelinde kullanılamaz.");
                        continue;
                    }
                    var p = FieldRules.Parse(f, raw, out var err);
                    if (err is not null) { Err(f.Label, err); continue; }
                    values[f.Id] = raw;
                    if (p is not null && f.Unique)
                    {
                        var key = FieldRules.UniqueKey(p.Text);
                        if (uniques.TryGetValue((f.Id, key), out var other)) Err(f.Label, $"{f.Label} değeri {p.Text} dosyada {other}. satırda da var.");
                        else uniques[(f.Id, key)] = r;
                        var used = await db.UniqueFieldValues.Where(u => u.FieldId == f.Id && u.Value == key).Join(db.Items, u => u.ItemId, i => i.Id, (u, i) => i.Tag).FirstOrDefaultAsync();
                        if (used is not null) Err(f.Label, $"{f.Label} değeri {p.Text} {used} kaydında kullanılıyor.");
                    }
                }
                if (errors.Count == before && model is not null && wh is not null)
                    rows.Add((r, model, wh, loc, serial, cost, string.IsNullOrWhiteSpace(V(HNotes)) ? null : V(HNotes)!.Trim(), values));
                if (errors.Count > 500) { errors.Add(new ImportError(r, null, "Çok fazla hata; ilk 500 hata gösteriliyor.")); break; }
            }

            var total = rows.Count + errors.Select(e => e.Row).Distinct().Count();
            var previewHeaders = new List<string> { "Satır", HModel, HWarehouse, HSerial }.Concat(fields.Take(4).Select(f => f.Label)).ToList();
            var preview = rows.Take(20).Select(x => new string?[] { x.Row.ToString(), $"{x.Model.Manufacturer!.Name} {x.Model.Name}", x.Wh.Name, x.Serial }
                .Concat(fields.Take(4).Select(f => x.Values.GetValueOrDefault(f.Id))).ToArray()).ToList();
            if (commit != true || errors.Count > 0 || rows.Count == 0)
                return Results.Ok(new ImportResult(total, rows.Count, false, errors, [], previewHeaders, preview));

            await using var tx = await db.Database.BeginTransactionAsync();
            var created = new List<string>();
            foreach (var x in rows)
            {
                var item = await ItemEndpoints.CreateAsync(db, a, x.Model, x.Wh, x.Loc, x.Serial, x.Cost, x.Notes, x.Values, "Excel ile eklendi");
                created.Add(item.Tag);
            }
            Audit.Add(db, a, "Excel ile ekleme", "Envanter", null, $"{cat.Name} · {created.Count} kayıt");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new ImportResult(total, rows.Count, true, [], created, previewHeaders, preview));
        }).DisableAntiforgery();

        api.MapGet("/import/employees/template", async (HttpContext ctx) =>
        {
            (await ctx.Access()).RequireWrite();
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Personel");
            string[] h = [HName + " *", HRegistry, HTitle, HDept, HEmail];
            for (var i = 0; i < h.Length; i++) { ws.Cell(1, i + 1).Value = h[i]; ws.Cell(1, i + 1).Style.Font.Bold = true; ws.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E6EEFC"); ws.Column(i + 1).Style.NumberFormat.Format = "@"; ws.Column(i + 1).Width = 28; }
            ws.SheetView.FreezeRows(1);
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return Results.File(ms.ToArray(), Excel.ContentType, "personel-sablonu.xlsx");
        });

        api.MapPost("/import/employees", async (Guid companyId, bool? commit, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            if (!a.CanWriteCompany(companyId)) throw AppException.Forbidden("Bu şirkete personel ekleme yetkiniz yok.");
            using var wb = await ReadWorkbook(ctx);
            var ws = wb.Worksheets.First();
            var errors = new List<ImportError>();
            var cols = Headers(ws, errors, [HName, HRegistry, HTitle, HDept, HEmail], [], [HName]);
            if (errors.Count > 0) return Results.Ok(new ImportResult(0, 0, false, errors, [], [], []));
            var existing = (await db.Employees.Where(e => e.CompanyId == companyId && e.RegistryNo != null).Select(e => new { e.RegistryNo, e.FullName }).ToListAsync())
                .ToDictionary(e => e.RegistryNo!, e => e.FullName);
            var seen = new Dictionary<string, int>();
            var rows = new List<Employee>();
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            if (lastRow - 1 > MaxRows) throw AppException.Invalid($"Tek seferde en fazla {MaxRows} satır yüklenebilir.");
            for (var r = 2; r <= lastRow; r++)
            {
                string? V(string h) => cols.TryGetValue(Norm(h), out var c) ? Read(ws.Cell(r, c)) : null;
                if (cols.Values.All(c => string.IsNullOrWhiteSpace(Read(ws.Cell(r, c))))) continue;
                var before = errors.Count;
                var name = V(HName); var reg = V(HRegistry); var mail = V(HEmail);
                if (string.IsNullOrWhiteSpace(name)) errors.Add(new(r, HName, "Ad soyad zorunludur."));
                if (!string.IsNullOrWhiteSpace(reg))
                {
                    if (existing.TryGetValue(reg, out var who)) errors.Add(new(r, HRegistry, $"{reg} sicil no bu şirkette {who} adına kayıtlı."));
                    if (seen.TryGetValue(reg, out var other)) errors.Add(new(r, HRegistry, $"{reg} sicil no dosyada {other}. satırda da var."));
                    seen[reg] = r;
                }
                if (!string.IsNullOrWhiteSpace(mail) && (!mail.Contains('@') || mail.Contains(' '))) errors.Add(new(r, HEmail, $"“{mail}” geçerli bir e-posta değil."));
                if (errors.Count == before)
                    rows.Add(new Employee { Id = Guid.NewGuid(), CompanyId = companyId, FullName = name!.Trim(), RegistryNo = Blank(reg), Title = Blank(V(HTitle)),
                        Department = Blank(V(HDept)), Email = Blank(mail)?.ToLowerInvariant(), Source = EmployeeSource.Manual, IsActive = true });
            }
            var total = rows.Count + errors.Select(e => e.Row).Distinct().Count();
            var preview = rows.Take(20).Select(e => new string?[] { e.FullName, e.RegistryNo, e.Title, e.Department, e.Email }).ToList();
            string[] ph = [HName, HRegistry, HTitle, HDept, HEmail];
            if (commit != true || errors.Count > 0 || rows.Count == 0) return Results.Ok(new ImportResult(total, rows.Count, false, errors, [], ph, preview));
            db.Employees.AddRange(rows);
            Audit.Add(db, a, "Excel ile ekleme", "Personel", null, $"{rows.Count} kişi");
            await db.SaveChangesAsync();
            return Results.Ok(new ImportResult(total, rows.Count, true, [], rows.Select(e => e.FullName).ToList(), ph, preview));
        }).DisableAntiforgery();
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static async Task<List<FieldDefinition>> ItemFields(AppDbContext db, Guid categoryId, List<Guid> modelIds) =>
        await db.FieldDefinitions.Where(f => !f.Archived && (f.CategoryId == categoryId || (f.AssetModelId != null && modelIds.Contains(f.AssetModelId.Value))))
            .OrderBy(f => f.AssetModelId != null).ThenBy(f => f.SortOrder).ThenBy(f => f.Label).ToListAsync();

    static string Header(FieldDefinition f, List<AssetModel> models)
    {
        if (f.AssetModelId is null) return f.Label + (f.Required ? " *" : "");
        var m = models.FirstOrDefault(x => x.Id == f.AssetModelId);
        return $"{f.Label} ({m?.Name})" + (f.Required ? " *" : "");
    }

    static async Task<List<Warehouse>> Writable(AppDbContext db, Access a)
    {
        var all = await a.WarehousesQuery(db).Where(w => !w.Archived).Include(w => w.Company).Include(w => w.Locations).OrderBy(w => w.Name).ToListAsync();
        return all.Where(w => a.CanWriteWarehouse(w.Id)).ToList();
    }

    static async Task<XLWorkbook> ReadWorkbook(HttpContext ctx)
    {
        if (!ctx.Request.HasFormContentType) throw AppException.Invalid("Excel dosyası seçin.");
        var form = await ctx.Request.ReadFormAsync();
        var file = form.Files.FirstOrDefault() ?? throw AppException.Invalid("Excel dosyası seçin.");
        if (file.Length > 10 * 1024 * 1024) throw AppException.Invalid("Dosya 10 MB sınırını aşıyor.");
        var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;
        try { return new XLWorkbook(ms); }
        catch (Exception ex) when (ex is not AppException)
        {
            throw AppException.Invalid("Dosya okunamadı. Şablondan oluşturulmuş bir .xlsx dosyası yükleyin (eski .xls biçimi desteklenmez).");
        }
    }

    /// <summary>Başlık satırını okur. Bilinmeyen dolu başlık hatadır (veri sessizce atlanmasın).</summary>
    static Dictionary<string, int> Headers(IXLWorksheet ws, List<ImportError> errors, string[] standard, IEnumerable<string> extra, string[] required)
    {
        var known = standard.Select(Norm).Concat(extra).ToHashSet();
        var cols = new Dictionary<string, int>();
        var last = ws.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (var c = 1; c <= last; c++)
        {
            var h = Norm(ws.Cell(1, c).GetString());
            if (h.Length == 0) continue;
            if (!known.Contains(h)) { errors.Add(new ImportError(1, ws.Cell(1, c).GetString(), $"Tanınmayan sütun: “{ws.Cell(1, c).GetString()}”. Güncel şablonu indirin.")); continue; }
            if (cols.ContainsKey(h)) { errors.Add(new ImportError(1, ws.Cell(1, c).GetString(), "Bu sütun iki kez var.")); continue; }
            cols[h] = c;
        }
        foreach (var r in required)
            if (!cols.ContainsKey(Norm(r))) errors.Add(new ImportError(1, r, $"“{r}” sütunu eksik. Güncel şablonu indirin."));
        return cols;
    }

    static string Norm(string? s) => (s ?? "").Replace("*", "").Trim().ToLower(Tr);

    /// <summary>Hücreyi metne çevirir. Sayı olarak girilmiş seri no / IMEI bilimsel gösterime dönüşmez.</summary>
    static string? Read(IXLCell cell, FieldType? type = null)
    {
        if (cell.IsEmpty()) return null;
        var v = cell.Value;
        if (v.IsDateTime) return v.GetDateTime().ToString(type is null or FieldType.Date ? "yyyy-MM-dd" : "dd.MM.yyyy", CultureInfo.InvariantCulture);
        if (v.IsNumber) return v.GetNumber().ToString("0.############", CultureInfo.InvariantCulture);
        if (v.IsBoolean) return v.GetBoolean() ? "true" : "false";
        var s = cell.GetString().Trim();
        return s.Length == 0 ? null : s;
    }
}

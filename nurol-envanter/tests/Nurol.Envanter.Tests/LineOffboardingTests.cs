using System.Net;
using System.Security.Cryptography;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Tests;

[Collection("app")]
public class LineOffboardingTests(TestApp app)
{
    record IdOnly(Guid Id);
    Guid Wh(string name) => app.Db(db => db.Warehouses.First(w => w.Name == name).Id);
    Guid Co(string code) => app.Db(db => db.Companies.First(c => c.Code == code).Id);

    static string RandomMobile() => "05" + RandomNumberGenerator.GetInt32(30, 60) + RandomNumberGenerator.GetInt32(1000000, 9999999);
    static string RandomIccid() => "8990" + string.Concat(Enumerable.Range(0, 15).Select(_ => RandomNumberGenerator.GetInt32(0, 10)));

    static PhoneLineSave Save(Guid company, string number, string? iccid = null, LineStatus status = LineStatus.Active, uint? version = null) =>
        new(company, number, "Turkcell", "Ses + Data", "Kurumsal 10 GB", 289.90m, iccid, status, "Test", null, null, version);

    async Task<PhoneLineDetail> NewLine(HttpClient c, Guid company, string? number = null, string? iccid = null)
    {
        var r = await c.PostJson("/api/lines", Save(company, number ?? RandomMobile(), iccid));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Read<PhoneLineDetail>();
    }

    async Task<Guid> NewItem(HttpClient admin)
    {
        var model = app.Db(db => db.AssetModels.First(m => m.Name == "27BR650B").Id);
        var res = await admin.PostJson("/api/items", new ItemSave(model, Wh("Ankara Merkez Depo"), null, "LN" + RandomNumberGenerator.GetHexString(10), null, null, [], null));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Read<IdOnly>()).Id;
    }

    async Task<Guid> NewEmployee(HttpClient admin, string name)
    {
        var r = await admin.PostJson("/api/employees", new EmployeeSave(Co("NRL"), "S" + RandomNumberGenerator.GetInt32(100000, 999999), name, "Uzman", "Test Birimi", null, true));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Read<IdOnly>()).Id;
    }

    [Fact]
    public async Task Lines_are_normalized_unique_scoped_and_follow_the_device_holder()
    {
        var admin = await app.Login("yonetici");
        var burak = await app.Login("burak.kaya");
        var zeynep = await app.Login("zeynep.celik");
        var raw = RandomMobile();
        var iccid = RandomIccid();

        // +90 ile, boşluklu yazılan numara 0'lı 11 haneye çevrilir; ICCID sonundaki F atılır.
        var line = await NewLine(burak, Co("NRL"), "+90 " + raw[1..4] + " " + raw[4..], iccid + "F");
        Assert.Equal(raw, line.Line.Number);
        Assert.Equal(iccid, line.Line.Iccid);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJson("/api/lines", Save(Co("NRL"), raw[1..]))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJson("/api/lines", Save(Co("NRL"), RandomMobile(), iccid))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJson("/api/lines", Save(Co("NRL"), RandomMobile(), "1234"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJson("/api/lines", Save(Co("NRL"), "12345"))).StatusCode);
        // Denetçi hat ekleyemez; depo sorumlusu yetkisi olmayan şirkete ekleyemez ve göremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await zeynep.PostJson("/api/lines", Save(Co("NRL"), RandomMobile()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await burak.PostJson("/api/lines", Save(Co("NMK"), RandomMobile()))).StatusCode);
        var other = await NewLine(admin, Co("NMK"));
        Assert.Equal(HttpStatusCode.NotFound, (await burak.GetAsync($"/api/lines/{other.Line.Id}")).StatusCode);
        Assert.DoesNotContain(await burak.GetJson<List<PhoneLineRow>>("/api/lines"), l => l.Id == other.Line.Id);
        // Başka şirketin cihazına takılamaz.
        var item = await NewItem(admin);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJson($"/api/lines/{other.Line.Id}/install", new LineInstall(item, null))).StatusCode);

        // Hat cihaza takılır, cihaz zimmetlenince hat da o kişide görünür ve tutanağa yazılır.
        var installed = await (await burak.PostJson($"/api/lines/{line.Line.Id}/install", new LineInstall(item, "SIM takıldı"))).Read<PhoneLineDetail>();
        Assert.Equal(item, installed.Line.ItemId);
        Assert.Null(installed.Line.HolderId);
        var emp = await NewEmployee(admin, "Hat Testi " + RandomNumberGenerator.GetHexString(4));
        Assert.Equal(HttpStatusCode.OK, (await burak.PostJson("/api/assignments", new AssignmentCreate(emp, [item], null, null))).StatusCode);
        var held = await burak.GetJson<List<PhoneLineRow>>($"/api/lines?employeeId={emp}");
        Assert.Single(held);
        Assert.Equal(emp, held[0].HolderId);
        Assert.Null(held[0].EmployeeId);
        var snapshot = app.Db(db => db.Assignments.Where(a => a.EmployeeId == emp).Select(a => a.Snapshot).First());
        Assert.Contains("Hat: " + LineLabels.Number(raw), snapshot);
        Assert.Contains(await burak.GetJson<List<PhoneLineRow>>("/api/lines?free=true"), l => l.Id != line.Line.Id);
        Assert.DoesNotContain(await burak.GetJson<List<PhoneLineRow>>("/api/lines?free=true"), l => l.Id == line.Line.Id);

        // Bir cihaza en fazla iki hat.
        var second = await NewLine(admin, Co("NRL"));
        var third = await NewLine(admin, Co("NRL"));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJson($"/api/lines/{second.Line.Id}/install", new LineInstall(item, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJson($"/api/lines/{third.Line.Id}/install", new LineInstall(item, null))).StatusCode);

        // Doğrudan verme, pasif personele verilememe, iptal edilince kişiden düşme.
        var given = await (await admin.PostJson($"/api/lines/{third.Line.Id}/assign", new LineAssign(emp, null))).Read<PhoneLineDetail>();
        Assert.Equal(emp, given.Line.EmployeeId);
        var cancelled = await (await admin.PutJson($"/api/lines/{third.Line.Id}", Save(Co("NRL"), third.Line.Number, status: LineStatus.Cancelled, version: given.Version))).Read<PhoneLineDetail>();
        Assert.Null(cancelled.Line.EmployeeId);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJson($"/api/lines/{third.Line.Id}/assign", new LineAssign(emp, null))).StatusCode);
        Assert.Contains(cancelled.Events, e => e.Type == "Geri alındı");

        // Eski sürümle kaydetme çakışma verir.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutJson($"/api/lines/{third.Line.Id}", Save(Co("NRL"), third.Line.Number, status: LineStatus.Cancelled, version: given.Version))).StatusCode);

        // Maliyet raporu.
        var report = await admin.GetJson<ReportTable>("/api/reports/hatlar?groupBy=operator");
        Assert.Contains(report.Rows, r => r[0]?.ToString() == "Turkcell");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/reports/hatlar?format=xlsx")).StatusCode);
    }

    [Fact]
    public async Task Line_import_validates_every_row_and_saves_all_or_nothing()
    {
        var admin = await app.Login("yonetici");
        var path = Path.Combine(Path.GetTempPath(), "ne-hat-" + Guid.NewGuid().ToString("N") + ".xlsx");
        File.WriteAllBytes(path, await admin.GetByteArrayAsync("/api/import/lines/template"));
        var existing = app.Db(db => db.PhoneLines.Select(l => l.Number).First());
        var n1 = RandomMobile(); var n2 = RandomMobile();
        byte[] Build(params string?[][] rows)
        {
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet("Hatlar");
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    if (rows[r][c] is string v) ws.Cell(r + 2, c + 1).Value = v;
            var outPath = Path.Combine(Path.GetTempPath(), "ne-hat-" + Guid.NewGuid().ToString("N") + ".xlsx");
            wb.SaveAs(outPath);
            return File.ReadAllBytes(outPath);
        }
        async Task<ImportResult> Up(byte[] x, bool commit)
        {
            var r = await admin.PostAsync($"/api/import/lines?companyId={Co("NRL")}&commit={commit.ToString().ToLowerInvariant()}",
                Http.File(x, "hat.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await r.Read<ImportResult>();
        }

        var bad = await Up(Build(
            [n1, "Turkcell", "Ses + Data", "Kurumsal", "250,50", null, "Aktif"],
            [existing, "Vodafone", "Yalnız data"],
            [n1, "Turkcell", "Ses + Data"],
            [RandomMobile(), "Avea", "Ses + Data"],
            [RandomMobile(), "Turkcell", "Ses + Data", null, null, "999"]), commit: true);
        Assert.False(bad.Committed);
        Assert.Contains(bad.Errors, e => e.Row == 3 && e.Column == "Numara");
        Assert.Contains(bad.Errors, e => e.Row == 4 && e.Message.Contains("2. satırda"));
        Assert.Contains(bad.Errors, e => e.Row == 5 && e.Column == "Operatör");
        Assert.Contains(bad.Errors, e => e.Row == 6 && e.Column == "ICCID");
        Assert.False(app.Db(db => db.PhoneLines.Any(l => l.Number == n1)));

        var good = Build([n1, "turkcell", "Ses + Data", "Kurumsal", "250,50", null, "Aktif"], [n2, "Türk Telekom", "M2M / IoT", null, null, null, "Askıda", null, "31.12.2027"]);
        var preview = await Up(good, commit: false);
        Assert.Equal(2, preview.Valid);
        Assert.False(preview.Committed);
        var saved = await Up(good, commit: true);
        Assert.True(saved.Committed);
        var l1 = app.Db(db => db.PhoneLines.First(l => l.Number == n1));
        Assert.Equal("Turkcell", l1.Operator);
        Assert.Equal(250.50m, l1.MonthlyFee);
        Assert.Equal(new DateOnly(2027, 12, 31), app.Db(db => db.PhoneLines.First(l => l.Number == n2).ContractEnd));
    }

    [Fact]
    public async Task Deactivating_an_employee_opens_offboarding_which_blocks_until_everything_is_returned()
    {
        var admin = await app.Login("yonetici");
        var burak = await app.Login("burak.kaya");
        var zeynep = await app.Login("zeynep.celik");
        var name = "Ayrılan " + RandomNumberGenerator.GetHexString(4);
        var emp = await NewEmployee(admin, name);
        var item = await NewItem(admin);
        Assert.Equal(HttpStatusCode.OK, (await burak.PostJson("/api/assignments", new AssignmentCreate(emp, [item], null, null))).StatusCode);
        var line = await NewLine(admin, Co("NRL"));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJson($"/api/lines/{line.Line.Id}/assign", new LineAssign(emp, null))).StatusCode);

        // Üzerinde hiçbir şey olmayan personel pasife alınınca süreç açılmaz.
        var empty = await NewEmployee(admin, "Boş " + RandomNumberGenerator.GetHexString(4));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutJson($"/api/employees/{empty}", new EmployeeSave(Co("NRL"), null, "Boş", null, null, null, false))).StatusCode);
        Assert.False(app.Db(db => db.OffboardingCases.Any(c => c.EmployeeId == empty)));

        // Hat taşıyan personelin şirketi değiştirilemez.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutJson($"/api/employees/{emp}", new EmployeeSave(Co("NMK"), null, name, null, null, null, true))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutJson($"/api/employees/{emp}", new EmployeeSave(Co("NRL"), null, name, "Uzman", "Test Birimi", null, false))).StatusCode);
        var cases = await burak.GetJson<List<OffboardingRow>>($"/api/offboarding?employeeId={emp}");
        var row = Assert.Single(cases);
        Assert.Equal(OffboardingStatus.Open, row.Status);
        Assert.Equal("Personel pasife alındı", row.Source);
        Assert.Equal(1, row.OpenItems);
        Assert.Equal(1, row.OpenLines);
        Assert.StartsWith("ILS-", row.No);
        Assert.Contains(app.Db(db => db.OutboxMails.Where(m => m.Kind == "İlişik kesme").Select(m => m.Subject).ToList()), s => s.Contains(name));

        // Aynı kişi için ikinci açık süreç olmaz.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJson("/api/offboarding", new OffboardingStart(emp, null, null))).StatusCode);

        var d = await burak.GetJson<OffboardingDetail>($"/api/offboarding/{row.Id}");
        Assert.True(d.CanManage);
        Assert.False(d.CanComplete);
        Assert.Equal(2, d.Blockers.Count);
        Assert.Single(d.Items);
        Assert.Single(d.Lines);
        Assert.Equal(HttpStatusCode.Conflict, (await burak.PostAsync($"/api/offboarding/{row.Id}/complete", null)).StatusCode);
        // Denetçi görür ama yönetemez.
        Assert.False((await zeynep.GetJson<OffboardingDetail>($"/api/offboarding/{row.Id}")).CanManage);
        Assert.Equal(HttpStatusCode.Forbidden, (await zeynep.PostJson($"/api/offboarding/{row.Id}/tasks", new OffboardingTaskSave("Kart"))).StatusCode);

        d = await (await burak.PostJson($"/api/offboarding/{row.Id}/tasks", new OffboardingTaskSave("Giriş kartı teslim alındı"))).Read<OffboardingDetail>();
        var task = Assert.Single(d.Tasks);
        Assert.Equal(HttpStatusCode.OK, (await burak.PostJson("/api/assignments/return", new AssignmentReturn(emp, [item], null, null, new Dictionary<Guid, string> { [item] = "Sağlam" }, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await burak.PostJson($"/api/lines/{line.Line.Id}/assign", new LineAssign(null, "İlişik kesme"))).StatusCode);
        d = await burak.GetJson<OffboardingDetail>($"/api/offboarding/{row.Id}");
        Assert.Equal(["1 görev tamamlanmadı."], d.Blockers);
        Assert.Equal(2, d.Returned.Count);

        d = await (await burak.PutJson($"/api/offboarding/{row.Id}/tasks/{task.Id}", new OffboardingTaskToggle(true))).Read<OffboardingDetail>();
        Assert.True(d.CanComplete);
        Assert.Equal(HttpStatusCode.NotFound, (await burak.GetAsync($"/api/offboarding/{row.Id}/certificate")).StatusCode);
        var done = await (await burak.PostAsync($"/api/offboarding/{row.Id}/complete", null)).Read<OffboardingDetail>();
        Assert.Equal(OffboardingStatus.Completed, done.Case.Status);
        Assert.True(done.HasCertificate);
        Assert.False(done.CanManage);
        var pdf = await burak.GetByteArrayAsync($"/api/offboarding/{row.Id}/certificate");
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Equal(app.Db(db => db.OffboardingCases.First(c => c.Id == row.Id).CertificateSha256), Convert.ToHexStringLower(SHA256.HashData(pdf)));
        // Kapanmış süreç değiştirilemez, tekrar kapatılamaz.
        Assert.Equal(HttpStatusCode.Conflict, (await burak.PostJson($"/api/offboarding/{row.Id}/tasks", new OffboardingTaskSave("Geç görev"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await burak.PostAsync($"/api/offboarding/{row.Id}/complete", null)).StatusCode);

        // Kapandıktan sonra yeni süreç elle açılabilir; iptal için neden gerekir.
        var again = await (await admin.PostJson("/api/offboarding", new OffboardingStart(emp, new DateOnly(2026, 11, 30), "Tekrar"))).Read<IdOnly>();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJson($"/api/offboarding/{again.Id}/cancel", new OffboardingCancel(""))).StatusCode);
        var cancelled = await (await admin.PostJson($"/api/offboarding/{again.Id}/cancel", new OffboardingCancel("Yanlışlıkla açıldı"))).Read<OffboardingDetail>();
        Assert.Equal(OffboardingStatus.Cancelled, cancelled.Case.Status);
        Assert.False(cancelled.HasCertificate);
    }

    [Fact]
    public async Task Offboarding_is_invisible_outside_company_scope()
    {
        var admin = await app.Login("yonetici");
        var burak = await app.Login("burak.kaya");
        var r = await admin.PostJson("/api/employees", new EmployeeSave(Co("NMK"), null, "Makina Personeli " + RandomNumberGenerator.GetHexString(3), null, null, null, true));
        var emp = (await r.Read<IdOnly>()).Id;
        var c = await (await admin.PostJson("/api/offboarding", new OffboardingStart(emp, null, null))).Read<IdOnly>();
        Assert.Equal(HttpStatusCode.NotFound, (await burak.GetAsync($"/api/offboarding/{c.Id}")).StatusCode);
        Assert.DoesNotContain(await burak.GetJson<List<OffboardingRow>>("/api/offboarding"), x => x.Id == c.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await burak.PostAsync($"/api/offboarding/{c.Id}/complete", null)).StatusCode);
        // Üzerinde bir şey yoksa ve görev yoksa hemen kapatılabilir.
        var done = await (await admin.PostAsync($"/api/offboarding/{c.Id}/complete", null)).Read<OffboardingDetail>();
        Assert.Equal(OffboardingStatus.Completed, done.Case.Status);
    }
}

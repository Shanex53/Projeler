using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Services;
using Nurol.Envanter.Shared;
using PdfSharp.Pdf.IO;

namespace Nurol.Envanter.Tests;

[Collection("app")]
public class Phase3Tests(TestApp app)
{
    Guid Cat(string code) => app.Db(db => db.Categories.First(c => c.Code == code).Id);
    Guid Wh(string name) => app.Db(db => db.Warehouses.First(w => w.Name == name).Id);

    static byte[] Bytes(XLWorkbook wb)
    {
        var path = Path.Combine(Path.GetTempPath(), "ne-yukleme-" + Guid.NewGuid().ToString("N") + ".xlsx");
        wb.SaveAs(path);
        return File.ReadAllBytes(path);
    }

    /// <summary>Şablonu düzenlemek için dosyadan açar (akıştan açılan kitap yeniden kaydedilirken kaynak akışa ihtiyaç duyar).</summary>
    static XLWorkbook Open(byte[] xlsx)
    {
        var path = Path.Combine(Path.GetTempPath(), "ne-sablon-" + Guid.NewGuid().ToString("N") + ".xlsx");
        File.WriteAllBytes(path, xlsx);
        return new XLWorkbook(path);
    }

    async Task<ImportResult> Upload(HttpClient c, string url, byte[] xlsx)
    {
        var r = await c.PostAsync(url, Http.File(xlsx, "dosya.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Read<ImportResult>();
    }

    [Fact]
    public async Task Reports_respect_scope_and_export_to_excel()
    {
        var burak = await app.Login("burak.kaya");
        var t = await burak.GetJson<ReportTable>("/api/reports/zimmet");
        var wIdx = t.Columns.ToList().FindIndex(c => c.Label == "Depo");
        Assert.NotEmpty(t.Rows);
        Assert.DoesNotContain(t.Rows, r => ((JsonElement)r[wIdx]!).GetString() == "İstanbul Şantiye Deposu");

        var xlsx = await burak.GetByteArrayAsync("/api/reports/zimmet?format=xlsx");
        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        var ws = wb.Worksheets.First();
        Assert.Equal("Personel bazlı zimmet", ws.Cell(1, 1).GetString());
        Assert.Contains(ws.RowsUsed(), r => r.Cell(1).GetString() == "Personel");
        Assert.DoesNotContain(ws.CellsUsed(), c => c.GetString() == "İstanbul Şantiye Deposu");

        var admin = await app.Login("yonetici");
        foreach (var key in new[] { "eksik-evrak", "sarf", "deger", "ayrilan", "hareket" })
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/reports/{key}?from=2026-01-01&to=2026-12-31")).StatusCode);
        var value = await admin.GetJson<ReportTable>("/api/reports/deger?groupBy=kategori");
        var expected = app.Db(db => db.Items.Where(i => i.Status != ItemStatus.Retired).Sum(i => i.Cost ?? 0));
        Assert.Equal(expected, value.Rows.Sum(r => ((JsonElement)r[3]!).GetDecimal()));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/reports/sarf?from=2024-01-01&to=2026-01-01")).StatusCode);
    }

    [Fact]
    public async Task Item_import_validates_everything_and_is_all_or_nothing()
    {
        var admin = await app.Login("yonetici");
        var tmpl = await admin.GetByteArrayAsync($"/api/import/items/template?categoryId={Cat("TLF")}");
        using var wb = Open(tmpl);
        var ws = wb.Worksheet("Envanter");
        int Col(string h) => ws.Row(1).CellsUsed().First(c => c.GetString().TrimEnd(' ', '*') == h).Address.ColumnNumber;
        var sn = RandomNumberGenerator.GetHexString(8);
        var imeiText = "0" + RandomNumberGenerator.GetInt32(0, int.MaxValue).ToString("D14"); // baştaki sıfır
        var imeiNum = 350_000_000_000_000L + RandomNumberGenerator.GetInt32(0, int.MaxValue);   // sayı olarak girilmiş
        void Row(int r, string model, string wh, string serial, XLCellValue imei, string? renk = null)
        {
            ws.Cell(r, Col("Model")).Value = model; ws.Cell(r, Col("Depo")).Value = wh; ws.Cell(r, Col("Seri no")).Value = serial;
            ws.Cell(r, Col("IMEI 1")).Value = imei;
            if (renk is not null) ws.Cell(r, Col("Renk")).Value = renk;
        }
        Row(2, "Samsung Galaxy A55", "Ankara Merkez Depo", "A" + sn, imeiText, "Siyah");
        Row(3, "Samsung Galaxy A55", "Olmayan Depo", "B" + sn, imeiNum);
        Row(4, "Samsung Galaxy A55", "Ankara Merkez Depo", "A" + sn, "123");               // dosya içi tekrar seri + kısa IMEI
        Row(5, "Apple iPhone 15", "İstanbul Şantiye Deposu", "C" + sn, imeiText, "Mor");   // tekrar IMEI + listede olmayan renk
        var before = app.Db(db => db.Items.Count());

        var res = await Upload(admin, $"/api/import/items?categoryId={Cat("TLF")}&commit=true", Bytes(wb));
        Assert.False(res.Committed);
        Assert.Contains(res.Errors, e => e.Row == 3 && e.Column == "Depo");
        Assert.Contains(res.Errors, e => e.Row == 4 && e.Column == "Seri no");
        Assert.Contains(res.Errors, e => e.Row == 4 && e.Column == "IMEI 1");
        Assert.Contains(res.Errors, e => e.Row == 5 && e.Column == "IMEI 1" && e.Message.Contains("2. satırda"));
        Assert.Contains(res.Errors, e => e.Row == 5 && e.Column == "Renk");
        Assert.Equal(before, app.Db(db => db.Items.Count())); // hiçbir şey kaydedilmedi

        // Hataları düzelt: yalnızca geçerli iki satır.
        ws.Row(5).Delete(); ws.Row(4).Delete();
        ws.Cell(3, Col("Depo")).Value = "Gölbaşı Fabrika Deposu";
        var preview = await Upload(admin, $"/api/import/items?categoryId={Cat("TLF")}", Bytes(wb));
        Assert.Empty(preview.Errors);
        Assert.Equal(2, preview.Valid);
        Assert.False(preview.Committed);
        Assert.Equal(before, app.Db(db => db.Items.Count()));

        var done = await Upload(admin, $"/api/import/items?categoryId={Cat("TLF")}&commit=true", Bytes(wb));
        Assert.True(done.Committed);
        Assert.Equal(2, done.Created.Count);
        Assert.All(done.Created, t => Assert.StartsWith("NRL-TLF-", t));
        var imeiField = app.Db(db => db.FieldDefinitions.First(f => f.Key == "imei_1").Id);
        Assert.True(app.Db(db => db.FieldValues.Any(v => v.FieldId == imeiField && v.Text == imeiText)));
        Assert.True(app.Db(db => db.FieldValues.Any(v => v.FieldId == imeiField && v.Text == imeiNum.ToString())));
    }

    [Fact]
    public async Task Item_import_rejects_unknown_columns_and_unauthorized_warehouses()
    {
        var burak = await app.Login("burak.kaya");
        var tmpl = await burak.GetByteArrayAsync($"/api/import/items/template?categoryId={Cat("MNT")}");
        using var wb = Open(tmpl);
        var ws = wb.Worksheet("Envanter");
        Assert.DoesNotContain(wb.Worksheet("Listeler").CellsUsed(), c => c.GetString() == "İstanbul Şantiye Deposu");
        ws.Cell(2, 1).Value = "LG 27BR650B"; ws.Cell(2, 2).Value = "İstanbul Şantiye Deposu"; ws.Cell(2, 4).Value = "X" + RandomNumberGenerator.GetHexString(8);
        var res = await Upload(burak, $"/api/import/items?categoryId={Cat("MNT")}", Bytes(wb));
        Assert.Contains(res.Errors, e => e.Column == "Depo" && e.Message.Contains("yetkiniz yok"));

        ws.Cell(1, 20).Value = "Bilinmeyen sütun";
        res = await Upload(burak, $"/api/import/items?categoryId={Cat("MNT")}", Bytes(wb));
        Assert.Contains(res.Errors, e => e.Row == 1 && e.Message.Contains("Tanınmayan sütun"));
    }

    [Fact]
    public async Task Employee_import_checks_registry_numbers()
    {
        var admin = await app.Login("yonetici");
        var co = app.Db(db => db.Companies.First(c => c.Code == "NRL").Id);
        using var wb = Open(await admin.GetByteArrayAsync("/api/import/employees/template"));
        var ws = wb.Worksheets.First();
        var reg = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        ws.Cell(2, 1).Value = "Toplu Bir"; ws.Cell(2, 2).Value = reg; ws.Cell(2, 5).Value = "toplu.bir@nurol.com.tr";
        ws.Cell(3, 1).Value = "Toplu İki"; ws.Cell(3, 2).Value = reg;
        ws.Cell(4, 1).Value = "Ayşe Benzeri"; ws.Cell(4, 2).Value = "10482"; // seed'de var
        var res = await Upload(admin, $"/api/import/employees?companyId={co}&commit=true", Bytes(wb));
        Assert.False(res.Committed);
        Assert.Equal(2, res.Errors.Count);
        ws.Row(4).Delete(); ws.Row(3).Delete();
        res = await Upload(admin, $"/api/import/employees?companyId={co}&commit=true", Bytes(wb));
        Assert.True(res.Committed);
        Assert.True(app.Db(db => db.Employees.Any(e => e.RegistryNo == reg && e.Email == "toplu.bir@nurol.com.tr")));
    }

    [Fact]
    public async Task Labels_render_pages_with_start_offset()
    {
        var admin = await app.Login("yonetici");
        var ids = app.Db(db => db.Items.OrderBy(i => i.Tag).Select(i => i.Id).Take(5).ToList());
        static int Pages(byte[] pdf) { using var d = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import); return d.PageCount; }

        var one = await admin.GetAsync($"/api/labels?ids={ids[0]}");
        Assert.Equal("application/pdf", one.Content.Headers.ContentType!.MediaType);
        var onePdf = await one.Content.ReadAsByteArrayAsync();
        Assert.Equal(1, Pages(onePdf));
        // QR görseli gerçekten gömülmüş olmalı (desteklenmeyen biçimde PDFsharp yer tutucu metin basar).
        using (var d = PdfReader.Open(new MemoryStream(onePdf), PdfDocumentOpenMode.Import))
        {
            var xobjects = d.Pages[0].Resources.Elements.GetDictionary("/XObject");
            Assert.NotNull(xobjects);
            Assert.Contains(xobjects.Elements.Values, v => (v as PdfSharp.Pdf.Advanced.PdfReference)?.Value is PdfSharp.Pdf.PdfDictionary x && x.Elements.GetName("/Subtype") == "/Image");
        }
        Assert.DoesNotContain("valid type", System.Text.Encoding.Latin1.GetString(onePdf));
        // 3x8 kâğıtta 22. konumdan başlayan 5 etiket iki sayfaya taşar.
        Assert.Equal(2, Pages(await admin.GetByteArrayAsync($"/api/labels?ids={string.Join(',', ids)}&layout=a4-3x8&start=22")));
        Assert.Equal(1, Pages(await admin.GetByteArrayAsync($"/api/labels?ids={string.Join(',', ids)}&layout=a4-2x7&start=1")));

        var ist = app.Db(db => db.Items.First(i => i.Warehouse!.Name == "İstanbul Şantiye Deposu").Id);
        var burak = await app.Login("burak.kaya");
        Assert.Equal(HttpStatusCode.NotFound, (await burak.GetAsync($"/api/labels?ids={ist}")).StatusCode);
    }

    [Fact]
    public async Task Lookup_finds_by_tag_serial_and_imei_within_scope()
    {
        var burak = await app.Login("burak.kaya");
        var phone = app.Db(db => db.Items.First(i => i.Serial == "R58X21KQ9PL"));
        Assert.Equal(phone.Id, (await burak.GetJson<LookupResult>($"/api/items/lookup?code={phone.Tag.ToLowerInvariant()}")).ItemId);
        Assert.Equal("Seri no", (await burak.GetJson<LookupResult>("/api/items/lookup?code=r58x21kq9pl")).MatchedBy);
        Assert.Equal("IMEI 1", (await burak.GetJson<LookupResult>("/api/items/lookup?code=352093114857261")).MatchedBy);
        Assert.Equal(HttpStatusCode.NotFound, (await burak.GetAsync("/api/items/lookup?code=YOK-123")).StatusCode);
        // Kapsam dışı cihaz bulunamaz.
        Assert.Equal(HttpStatusCode.NotFound, (await burak.GetAsync("/api/items/lookup?code=G6TZ81PQ0D")).StatusCode);
    }

    [Fact]
    public async Task Transfer_notifies_target_warehouse_users_and_digest_is_not_duplicated()
    {
        var admin = await app.Login("yonetici");
        var model = app.Db(db => db.AssetModels.First(m => m.Name == "27BR650B").Id);
        var item = (await (await admin.PostJson("/api/items", new ItemSave(model, Wh("Ankara Merkez Depo"), null, "NT" + RandomNumberGenerator.GetHexString(8), null, null, [], null))).Read<JsonElement>()).GetProperty("id").GetGuid();
        var t = await (await admin.PostJson("/api/transfers", new TransferCreate(Wh("Ankara Merkez Depo"), Wh("İstanbul Şantiye Deposu"), [item], [], null))).Read<TransferDetail>();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/transfers/{t.Header.Id}/send", null)).StatusCode);

        var to = app.Db(db => db.OutboxMails.Where(m => m.DedupKey!.StartsWith($"transfer-sent:{t.Header.Id}:")).Select(m => m.ToEmail).ToList());
        Assert.Contains("santiye.ist@nurol.com.tr", to);   // hedef depo sorumlusu
        Assert.Contains("selin.aydin@nurol.com.tr", to);   // şirket yöneticisi
        Assert.DoesNotContain("burak.kaya@nurol.com.tr", to); // hedef depoda yetkisi yok
        Assert.DoesNotContain("zeynep.celik@nurol.com.tr", to); // denetçi

        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(RandomNumberGenerator.GetInt32(1000, 9000)));
        int Run() { using var scope = app.Factory.Services.CreateScope(); return scope.ServiceProvider.GetRequiredService<Notifier>().DailyDigestAsync(day).GetAwaiter().GetResult(); }
        var first = Run();
        Assert.True(first > 0);
        Assert.Equal(0, Run());
        var ist = app.Db(db => db.OutboxMails.First(m => m.DedupKey == $"digest:{day:yyyy-MM-dd}:" + db.Users.First(u => u.UserName == "santiye.ist").Id));
        Assert.Contains("Teslim almanızı bekleyen transfer", ist.BodyHtml);
        Assert.Contains(t.Header.No, ist.BodyHtml);
    }

    [Fact]
    public async Task Account_settings_and_password_change()
    {
        var c = await app.Login("zeynep.celik");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutJson("/api/account", new MyAccountSave("gecersiz", true, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutJson("/api/account", new MyAccountSave("zeynep.yeni@nurol.com.tr", false, null, null))).StatusCode);
        var me = await c.GetJson<MyAccountDto>("/api/account");
        Assert.Equal("zeynep.yeni@nurol.com.tr", me.Email);
        Assert.False(me.NotifyByEmail);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutJson("/api/account", new MyAccountSave(me.Email, false, "yanlis", "YeniParola2026"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutJson("/api/account", new MyAccountSave(me.Email, true, "Gelistirme123", "YeniParola2026"))).StatusCode);
        // Parolayı geri al; diğer testler örnek parolayı kullanır.
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutJson("/api/account", new MyAccountSave("zeynep.celik@nurol.com.tr", true, "YeniParola2026", "Gelistirme123"))).StatusCode);
    }
}

/// <summary>Gerçek SMTP sunucusuna (Mailpit) gönderim ve hata durumunda yeniden deneme.</summary>
[Collection("app")]
public class MailTests(TestApp app)
{
    [Fact]
    public async Task Outbox_is_delivered_over_smtp_and_failures_are_retried()
    {
        await using var mailpit = new ContainerBuilder().WithImage("axllent/mailpit:v1.21.8")
            .WithPortBinding(1025, true).WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/api/v1/messages"))).Build();
        await mailpit.StartAsync();

        var subject = "Test " + RandomNumberGenerator.GetHexString(6);
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Önceki testlerin sıradaki e-postalarını bu teste karıştırmamak için gönderilmiş say.
        await db.OutboxMails.Where(m => m.SentAt == null).ExecuteUpdateAsync(u => u.SetProperty(m => m.SentAt, DateTimeOffset.UtcNow));
        db.OutboxMails.Add(new OutboxMail { Id = Guid.NewGuid(), Kind = "Test", ToEmail = "alici@nurol.com.tr", ToName = "Alıcı Çağrı", Subject = subject,
            BodyHtml = Mail.Html("Merhaba", ["Türkçe karakterler: ğüşıöç"], ("Aç", "https://envanter.local/")), CreatedAt = DateTimeOffset.UtcNow, SendAfter = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        // Yanlış port: gönderilemez, deneme sayısı artar, ileri bir zamana ertelenir.
        var bad = new SmtpOptions { Enabled = true, Host = mailpit.Hostname, Port = 1, Security = "None", From = "envanter@nurol.com.tr" };
        Assert.Equal(0, await MailSender.SendBatchAsync(db, bad, NullLogger.Instance));
        db.ChangeTracker.Clear();
        var m = await db.OutboxMails.FirstAsync(x => x.Subject == subject);
        Assert.Equal(1, m.Attempts);
        Assert.NotNull(m.LastError);
        Assert.True(m.SendAfter > DateTimeOffset.UtcNow);

        await db.OutboxMails.Where(x => x.Id == m.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.SendAfter, DateTimeOffset.UtcNow));
        var good = new SmtpOptions { Enabled = true, Host = mailpit.Hostname, Port = mailpit.GetMappedPublicPort(1025), Security = "None", From = "envanter@nurol.com.tr" };
        Assert.Equal(1, await MailSender.SendBatchAsync(db, good, NullLogger.Instance));
        Assert.Equal(0, await MailSender.SendBatchAsync(db, good, NullLogger.Instance)); // tekrar gönderilmez

        using var http = new HttpClient();
        var list = await http.GetFromJsonAsync<JsonElement>($"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(8025)}/api/v1/messages");
        var msg = list.GetProperty("messages").EnumerateArray().Single(x => x.GetProperty("Subject").GetString() == subject);
        Assert.Equal("alici@nurol.com.tr", msg.GetProperty("To")[0].GetProperty("Address").GetString());
    }
}

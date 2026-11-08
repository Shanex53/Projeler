using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Tests;

[Collection("app")]
public class ApiTests(TestApp app)
{
    Guid ItemId(string tag) => app.Db(db => db.Items.Where(i => i.Tag == tag).Select(i => i.Id).First());
    Guid EmployeeId(string name) => app.Db(db => db.Employees.Where(e => e.FullName == name).Select(e => e.Id).First());
    Guid WarehouseId(string name) => app.Db(db => db.Warehouses.Where(w => w.Name == name).Select(w => w.Id).First());
    Guid FieldId(string key) => app.Db(db => db.FieldDefinitions.Where(f => f.Key == key).Select(f => f.Id).First());
    Guid ModelId(string name) => app.Db(db => db.AssetModels.Where(m => m.Name == name).Select(m => m.Id).First());

    /// <summary>Testler birbirini etkilemesin diye her test kendi cihazını oluşturur.</summary>
    async Task<Guid> NewPhone(HttpClient admin, string warehouse = "Ankara Merkez Depo", string? imei = null)
    {
        imei ??= "35" + RandomNumberGenerator.GetInt32(0, int.MaxValue).ToString("D13");
        var res = await admin.PostJson("/api/items", new ItemSave(ModelId("Galaxy A55"), WarehouseId(warehouse), null,
            "SN" + Guid.NewGuid().ToString("N")[..10], null, null, new Dictionary<Guid, string?> { [FieldId("imei_1")] = imei }, null));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Read<CreatedItem>()).Id;
    }
    record CreatedItem(Guid Id, string Tag);

    [Fact]
    public void Tests_use_isolated_database()
    {
        var cs = app.Db(db => db.Database.GetConnectionString());
        Assert.Equal(new Npgsql.NpgsqlConnectionStringBuilder(app.ConnectionString).Database, new Npgsql.NpgsqlConnectionStringBuilder(cs).Database);
        Assert.Equal(new Npgsql.NpgsqlConnectionStringBuilder(app.ConnectionString).Port, new Npgsql.NpgsqlConnectionStringBuilder(cs).Port);
    }

    [Fact]
    public async Task Requires_login_and_csrf_header()
    {
        var anon = app.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/items")).StatusCode);

        var c = await app.Login("yonetici");
        c.DefaultRequestHeaders.Remove("X-NE-Request");
        var res = await c.PostJson("/api/employees", new EmployeeSave(Guid.NewGuid(), null, "X", null, null, null, true));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_with_generic_message()
    {
        var c = app.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-NE-Request", "1");
        var res = await c.PostJson("/api/auth/login", new LoginRequest("burak.kaya", "yanlis-parola"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains("Kullanıcı adı veya parola hatalı", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Warehouse_manager_sees_only_assigned_warehouses()
    {
        var c = await app.Login("burak.kaya"); // Ankara Merkez + Gölbaşı
        var list = await c.GetJson<PagedResult<ItemListRow>>("/api/items?pageSize=500");
        Assert.NotEmpty(list.Items);
        Assert.DoesNotContain(list.Items, i => i.WarehouseName == "İstanbul Şantiye Deposu");

        var istanbulItem = app.Db(db => db.Items.First(i => i.Warehouse!.Name == "İstanbul Şantiye Deposu" && i.OpenAssignmentId != null));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/items/{istanbulItem.Id}")).StatusCode);

        // Kapsam dışı depodaki tutanak ve belgesi de indirilemez.
        var doc = app.Db(db => db.Documents.First(d => d.AssignmentId == istanbulItem.OpenAssignmentId));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/documents/{doc.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/assignments/{istanbulItem.OpenAssignmentId}")).StatusCode);

        var csv = await (await c.GetAsync("/api/items/export")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("İstanbul Şantiye Deposu", csv);
        Assert.Contains("Ankara Merkez Depo", csv);
    }

    [Fact]
    public async Task Auditor_cannot_change_data()
    {
        var c = await app.Login("zeynep.celik");
        var res = await c.PostJson("/api/items", new ItemSave(ModelId("Galaxy A55"), WarehouseId("Ankara Merkez Depo"), null, "X1", null, null, [], null));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var list = await c.GetJson<PagedResult<ItemListRow>>("/api/items");
        Assert.NotEmpty(list.Items);
    }

    [Fact]
    public async Task Assignment_to_other_company_employee_is_rejected()
    {
        var admin = await app.Login("yonetici");
        var item = await NewPhone(admin);
        var res = await admin.PostJson("/api/assignments", new AssignmentCreate(EmployeeId("Murat Aydemir"), [item], null, null));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("aynı şirketin personeline", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Concurrent_assignments_of_same_item_only_one_succeeds()
    {
        var admin = await app.Login("yonetici");
        var item = await NewPhone(admin);
        var a = await app.Login("yonetici");
        var b = await app.Login("selin.aydin");
        var results = await Task.WhenAll(
            a.PostJson("/api/assignments", new AssignmentCreate(EmployeeId("Kemal Arslan"), [item], null, null)),
            b.PostJson("/api/assignments", new AssignmentCreate(EmployeeId("Zeynep Çelik"), [item], null, null)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, app.Db(db => db.AssignmentLines.Count(l => l.ItemId == item && l.IsOpen)));
    }

    [Fact]
    public async Task Repeated_request_with_same_idempotency_key_creates_one_assignment()
    {
        var admin = await app.Login("yonetici");
        var item = await NewPhone(admin);
        var key = Guid.NewGuid().ToString("N");
        var body = new AssignmentCreate(EmployeeId("Kemal Arslan"), [item], null, null);
        var r = await Task.WhenAll(admin.PostJson("/api/assignments", body, key), admin.PostJson("/api/assignments", body, key));
        Assert.All(r, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        var ids = await Task.WhenAll(r.Select(x => x.Read<AssignmentDetail>()));
        Assert.Equal(ids[0].Header.Id, ids[1].Header.Id);
        Assert.Equal(1, app.Db(db => db.Assignments.Count(x => x.IdempotencyKey == key)));
    }

    [Fact]
    public async Task Return_and_handover_close_and_open_lines()
    {
        var admin = await app.Login("yonetici");
        var item = await NewPhone(admin);
        var kemal = EmployeeId("Kemal Arslan");
        var zeynep = EmployeeId("Zeynep Çelik");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJson("/api/assignments", new AssignmentCreate(kemal, [item], null, null))).StatusCode);
        var hand = await admin.PostJson("/api/assignments/handover", new { fromEmployeeId = kemal, toEmployeeId = zeynep, itemIds = new[] { item }, note = "Görev değişikliği" });
        Assert.Equal(HttpStatusCode.OK, hand.StatusCode);
        var detail = await admin.GetJson<ItemDetail>($"/api/items/{item}");
        Assert.Equal(ItemStatus.Assigned, detail.Status);
        Assert.Equal(zeynep, detail.EmployeeId);
        Assert.Equal(3, app.Db(db => db.AssignmentLines.Count(l => l.ItemId == item)));
        Assert.Equal(1, app.Db(db => db.AssignmentLines.Count(l => l.ItemId == item && l.IsOpen)));

        var ret = await admin.PostJson("/api/assignments/return", new AssignmentReturn(zeynep, [item], null, null, null, null));
        Assert.Equal(HttpStatusCode.OK, ret.StatusCode);
        detail = await admin.GetJson<ItemDetail>($"/api/items/{item}");
        Assert.Equal(ItemStatus.InStock, detail.Status);
        Assert.Null(detail.EmployeeId);
        // Her olay ayrı tutanak üretir; ilk zimmetin belgesi korunur.
        Assert.Equal(4, detail.Documents.Count(d => d.Kind == DocumentKind.Generated));
    }

    [Fact]
    public async Task Generated_document_does_not_change_after_employee_rename()
    {
        var admin = await app.Login("yonetici");
        var item = await NewPhone(admin);
        var emp = await admin.PostJson("/api/employees", new EmployeeSave(app.Db(db => db.Companies.First(c => c.Code == "NRL").Id), null, "Deniz Aksoy", "Teknisyen", "Bakım", null, true));
        var empId = (await emp.Read<IdOnly>()).Id;
        var a = await (await admin.PostJson("/api/assignments", new AssignmentCreate(empId, [item], null, null))).Read<AssignmentDetail>();
        var before = await admin.GetByteArrayAsync($"/api/assignments/{a.Header.Id}/pdf");
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(before, 0, 4));

        await admin.PutJson($"/api/employees/{empId}", new EmployeeSave(app.Db(db => db.Companies.First(c => c.Code == "NRL").Id), null, "Deniz Aksoy Yılmaz", "Teknisyen", "Bakım", null, true));
        var after = await admin.GetByteArrayAsync($"/api/assignments/{a.Header.Id}/pdf");
        Assert.Equal(SHA256.HashData(before), SHA256.HashData(after));
        var snap = app.Db(db => db.Assignments.First(x => x.Id == a.Header.Id).Snapshot);
        Assert.Contains("Deniz Aksoy", snap);
        Assert.DoesNotContain("Yılmaz", snap);
    }
    record IdOnly(Guid Id);

    [Fact]
    public async Task Signed_upload_is_versioned_and_type_checked()
    {
        var admin = await app.Login("yonetici");
        var item = await NewPhone(admin);
        var a = await (await admin.PostJson("/api/assignments", new AssignmentCreate(EmployeeId("Kemal Arslan"), [item], null, null))).Read<AssignmentDetail>();
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n% test\n");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/assignments/{a.Header.Id}/signed", Http.File(pdf, "tarama.pdf", "application/pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/assignments/{a.Header.Id}/signed", Http.File(png, "tarama.png", "image/png"))).StatusCode);
        // Uzantısı PDF olsa da içeriği PDF olmayan dosya reddedilir.
        var fake = await admin.PostAsync($"/api/assignments/{a.Header.Id}/signed", Http.File("MZ-not-a-pdf"u8.ToArray(), "kotu.pdf", "application/pdf"));
        Assert.Equal(HttpStatusCode.BadRequest, fake.StatusCode);

        var d = await admin.GetJson<AssignmentDetail>($"/api/assignments/{a.Header.Id}");
        var signed = d.Documents.Where(x => x.Kind == DocumentKind.Signed).OrderBy(x => x.Version).ToList();
        Assert.Equal([1, 2], signed.Select(x => x.Version));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(pdf)), signed[0].Sha256);
        Assert.Equal(pdf, await admin.GetByteArrayAsync($"/api/documents/{signed[0].Id}"));
        Assert.True(d.Header.SignedUploaded);
    }

    [Fact]
    public async Task Stock_cannot_go_negative_even_concurrently()
    {
        var admin = await app.Login("yonetici");
        var company = app.Db(db => db.Companies.First(c => c.Code == "NRL").Id);
        var wh = WarehouseId("Ankara Merkez Depo");
        var created = await admin.PostJson("/api/stock", new StockItemSave(company, "TST-" + Guid.NewGuid().ToString("N")[..6], "Test malzemesi", "adet", 0));
        var sid = (await created.Read<IdOnly>()).Id;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJson("/api/stock/movements", new StockMovementRequest(sid, wh, StockMovementType.In, 10, null, null, null))).StatusCode);

        var tooMuch = await admin.PostJson("/api/stock/movements", new StockMovementRequest(sid, wh, StockMovementType.Out, 11, null, "Proje", null));
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Contains("10 adet", await tooMuch.Content.ReadAsStringAsync());

        var outs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            admin.PostJson("/api/stock/movements", new StockMovementRequest(sid, wh, StockMovementType.Out, 3, null, "Proje", null))));
        Assert.Equal(3, outs.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1m, app.Db(db => db.StockBalances.First(b => b.StockItemId == sid && b.WarehouseId == wh).Quantity));
        Assert.Equal(1m, app.Db(db => db.StockMovements.Where(m => m.StockItemId == sid).Sum(m => m.Quantity)));
    }

    [Fact]
    public async Task Stock_movement_idempotency_key_prevents_duplicates()
    {
        var admin = await app.Login("yonetici");
        var company = app.Db(db => db.Companies.First(c => c.Code == "NRL").Id);
        var wh = WarehouseId("Ankara Merkez Depo");
        var sid = (await (await admin.PostJson("/api/stock", new StockItemSave(company, "IDM-" + Guid.NewGuid().ToString("N")[..6], "Idem", "adet", 0))).Read<IdOnly>()).Id;
        var key = Guid.NewGuid().ToString("N");
        var body = new StockMovementRequest(sid, wh, StockMovementType.In, 5, null, null, null);
        await Task.WhenAll(admin.PostJson("/api/stock/movements", body, key), admin.PostJson("/api/stock/movements", body, key), admin.PostJson("/api/stock/movements", body, key));
        Assert.Equal(5m, app.Db(db => db.StockBalances.First(b => b.StockItemId == sid).Quantity));
        Assert.Equal(1, app.Db(db => db.StockMovements.Count(m => m.StockItemId == sid)));
    }

    [Fact]
    public async Task Imei_rules_are_enforced_and_leading_zeros_kept()
    {
        var admin = await app.Login("yonetici");
        var imei = "0" + RandomNumberGenerator.GetInt32(0, int.MaxValue).ToString("D14");
        var item = await NewPhone(admin, imei: imei);
        var detail = await admin.GetJson<ItemDetail>($"/api/items/{item}");
        Assert.Equal(imei, detail.Fields.First(f => f.Key == "imei_1").Value);

        // Aynı IMEI başka şirkette de kullanılamaz (grup geneli).
        var dup = await admin.PostJson("/api/items", new ItemSave(ModelId("Galaxy A55"), WarehouseId("Makina Merkez Depo"), null, "SNX" + Guid.NewGuid().ToString("N")[..8], null, null,
            new Dictionary<Guid, string?> { [FieldId("imei_1")] = imei }, null));
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
        Assert.Contains("başka bir kayıtta", await dup.Content.ReadAsStringAsync());

        var shortImei = await admin.PostJson("/api/items", new ItemSave(ModelId("Galaxy A55"), WarehouseId("Ankara Merkez Depo"), null, "SNY" + Guid.NewGuid().ToString("N")[..8], null, null,
            new Dictionary<Guid, string?> { [FieldId("imei_1")] = "12345" }, null));
        Assert.Equal(HttpStatusCode.BadRequest, shortImei.StatusCode);
        Assert.Contains("15 karakter", await shortImei.Content.ReadAsStringAsync());

        var missing = await admin.PostJson("/api/items", new ItemSave(ModelId("Galaxy A55"), WarehouseId("Ankara Merkez Depo"), null, "SNZ" + Guid.NewGuid().ToString("N")[..8], null, null, [], null));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("zorunludur", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Used_field_is_archived_not_deleted_and_values_survive()
    {
        var admin = await app.Login("yonetici");
        var cat = app.Db(db => db.Categories.First(c => c.Code == "TLF").Id);
        var created = await admin.PostJson("/api/fields", new FieldDefinitionSave("Kılıf rengi", null, FieldType.Text, cat, null, 9, false, false, 50, false, null, null));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var field = await created.Read<FieldDefinitionDto>();

        var item = await NewPhone(admin);
        var d = await admin.GetJson<ItemDetail>($"/api/items/{item}");
        var values = d.Fields.Where(f => !f.Archived).ToDictionary(f => f.FieldId, f => f.Value);
        values[field.Id] = "Lacivert";
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutJson($"/api/items/{item}", new ItemSave(ModelId("Galaxy A55"), d.WarehouseId, null, d.Serial, null, null, values, d.Version))).StatusCode);

        // Zorunlu yapmak mevcut boş kayıtlarla çelişir: kaydedilmez, çelişen kayıtlar döner.
        var strict = await admin.PutJson($"/api/fields/{field.Id}", new FieldDefinitionSave("Kılıf rengi", null, FieldType.Text, cat, null, 9, true, false, 50, false, null, null));
        Assert.Equal(HttpStatusCode.Conflict, strict.StatusCode);
        Assert.Contains("conflicts", await strict.Content.ReadAsStringAsync());

        var del = await admin.DeleteAsync($"/api/fields/{field.Id}");
        Assert.Contains("\"archived\":true", await del.Content.ReadAsStringAsync());
        d = await admin.GetJson<ItemDetail>($"/api/items/{item}");
        var archived = d.Fields.Single(f => f.FieldId == field.Id);
        Assert.True(archived.Archived);
        Assert.Equal("Lacivert", archived.Value);

        // Hiç kullanılmamış alan gerçekten silinir.
        var unused = await (await admin.PostJson("/api/fields", new FieldDefinitionSave("Geçici alan", null, FieldType.Text, cat, null, 10, false, false, null, false, null, null))).Read<FieldDefinitionDto>();
        Assert.Contains("\"archived\":false", await (await admin.DeleteAsync($"/api/fields/{unused.Id}")).Content.ReadAsStringAsync());
        Assert.False(app.Db(db => db.FieldDefinitions.Any(f => f.Id == unused.Id)));
    }

    [Fact]
    public async Task Concurrent_edit_with_stale_version_is_rejected()
    {
        var admin = await app.Login("yonetici");
        var item = await NewPhone(admin);
        var d = await admin.GetJson<ItemDetail>($"/api/items/{item}");
        var values = d.Fields.Where(f => !f.Archived).ToDictionary(f => f.FieldId, f => f.Value);
        var body = new ItemSave(d.AssetModelId, d.WarehouseId, null, d.Serial, 100, "ilk", values, d.Version);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutJson($"/api/items/{item}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutJson($"/api/items/{item}", body with { Notes = "ikinci" })).StatusCode);
    }

    [Fact]
    public async Task Session_works_across_instances_and_is_revoked_on_deactivation()
    {
        var admin = await app.Login("yonetici");
        var company = app.Db(db => db.Companies.First(c => c.Code == "NRL").Id);
        var name = "test." + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJson("/api/users", new UserCreate(name, "Test Kullanıcı", "Gelistirme123", Roles.Auditor, [new ScopeSave(company, null)]))).StatusCode);

        var user = await app.Login(name);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/auth/me")).StatusCode);

        // İkinci "pod": aynı veritabanı, ayrı süreç içi uygulama. Cookie orada da geçerli olmalı.
        await using var second = app.Create();
        var other = second.CreateClient(new() { HandleCookies = false });
        var cookie = await CookieOf(user);
        other.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/auth/me")).StatusCode);

        var id = app.Db(db => db.Users.First(u => u.UserName == name).Id);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutJson($"/api/users/{id}", new UserUpdate("Test Kullanıcı", false, Roles.Auditor, [new ScopeSave(company, null)], null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/auth/me")).StatusCode);
    }

    async Task<string> CookieOf(HttpClient c)
    {
        // Test istemcisi cookie'yi kendi içinde tutar; aynı kullanıcı için yeni bir girişle başlığı alırız.
        var raw = app.Factory.CreateClient(new() { HandleCookies = false });
        raw.DefaultRequestHeaders.Add("X-NE-Request", "1");
        var me = await c.GetJson<MeDto>("/api/auth/me");
        var res = await raw.PostJson("/api/auth/login", new LoginRequest(me.UserName, "Gelistirme123"));
        var set = res.Headers.GetValues("Set-Cookie").First(v => v.StartsWith("ne_oturum="));
        // Bu oturumu da kullanıcıya ait say; devre dışı bırakmada tüm oturumlar iptal edilir.
        return set.Split(';')[0];
    }

    [Fact]
    public async Task Dashboard_counts_match_scope()
    {
        var burak = await app.Login("burak.kaya");
        var d = await burak.GetJson<DashboardDto>("/api/dashboard");
        Assert.DoesNotContain(d.Warehouses, w => w.Name == "İstanbul Şantiye Deposu");
        var expected = app.Db(db => db.Items.Count(i => (i.Warehouse!.Name == "Ankara Merkez Depo" || i.Warehouse.Name == "Gölbaşı Fabrika Deposu") && i.Status != ItemStatus.Retired));
        Assert.Equal(expected, d.TotalItems);
    }
}

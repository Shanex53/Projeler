using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Tests;

/// <summary>Bellekte çalışan dizin. Gerçek LDAP davranışı LdapTests'te OpenLDAP ile ayrıca sınanır.</summary>
public class FakeDirectory : IDirectoryClient
{
    public ConcurrentDictionary<string, DirectoryPerson> People { get; } = new();
    public ConcurrentDictionary<string, string> Passwords { get; } = new();
    public bool Down { get; set; }

    public void Add(DirectoryPerson p, string? password = null)
    {
        People[p.Id] = p;
        if (password is not null) Passwords[p.Dn] = password;
    }

    public Task<IReadOnlyList<DirectoryPerson>> SearchAllAsync(CancellationToken ct = default) =>
        Down ? throw new DirectoryUnavailableException("Test: dizin kapalı") : Task.FromResult<IReadOnlyList<DirectoryPerson>>(People.Values.ToList());

    public Task<DirectoryPerson?> FindUserAsync(string userName, CancellationToken ct = default) =>
        Down ? throw new DirectoryUnavailableException("Test: dizin kapalı")
             : Task.FromResult(People.Values.FirstOrDefault(p => string.Equals(p.UserName, userName, StringComparison.OrdinalIgnoreCase)));

    public Task<bool> VerifyPasswordAsync(string dn, string password, CancellationToken ct = default) =>
        Down ? throw new DirectoryUnavailableException("Test: dizin kapalı")
             : Task.FromResult(!string.IsNullOrEmpty(password) && Passwords.TryGetValue(dn, out var p) && p == password);
}

[Collection("app")]
public class DirectoryTests(TestApp app)
{
    /// <summary>Her test kendi alan adı ve şirket metniyle çalışır; testler birbirinin personelini etkilemez.</summary>
    (WebApplicationFactory<Program> factory, FakeDirectory dir, string domain, string companyText) Setup()
    {
        var dir = new FakeDirectory();
        var domain = "test-" + RandomNumberGenerator.GetHexString(6).ToLowerInvariant() + ".local";
        var f = app.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Directory:Enabled", "true");
            b.UseSetting("Directory:LoginEnabled", "true");
            b.UseSetting("Directory:Server", "dc.test.local");
            b.UseSetting("Directory:BaseDn", "DC=test,DC=local");
            b.UseSetting("Directory:Domain", domain);
            b.ConfigureTestServices(s => s.AddSingleton<IDirectoryClient>(dir));
        });
        return (f, dir, domain, "Nurol Test " + domain);
    }

    static DirectoryPerson P(string user, string name, string company, bool disabled = false, string? id = null, string? dept = "Bilgi İşlem", string? registry = null) =>
        new(id ?? Guid.NewGuid().ToString(), $"CN={name},OU=Personel,DC=test,DC=local", user, name, user + "@nurol.com.tr", "Uzman", dept, company, registry, disabled);

    async Task<HttpClient> Admin(WebApplicationFactory<Program> f, string companyText)
    {
        var c = await app.Login("yonetici", f);
        var nrl = app.Db(db => db.Companies.First(x => x.Code == "NRL").Id);
        Assert.Equal(HttpStatusCode.OK, (await c.PostJson("/api/directory/rules", new DirectoryRuleSave(1, "company", DirectoryMatch.Equals, companyText, nrl))).StatusCode);
        return c;
    }

    async Task<DirectorySyncRunDto> Sync(HttpClient c, bool preview = false)
    {
        var r = await c.PostAsync($"/api/directory/sync?preview={preview.ToString().ToLowerInvariant()}", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Read<DirectorySyncRunDto>();
    }

    Employee? Emp(string domain, string id) => app.Db(db => db.Employees.FirstOrDefault(e => e.DirectoryId == domain + ":" + id));

    [Fact]
    public async Task Sync_creates_updates_and_never_duplicates_on_rename()
    {
        var (f, dir, domain, co) = Setup();
        await using var _ = f;
        var admin = await Admin(f, co);
        var ayse = P("ayse.test", "Ayşe Test", co);
        dir.Add(ayse);
        dir.Add(P("yabanci.kisi", "Yabancı Kişi", "Başka Firma A.Ş."));

        var preview = await Sync(admin, preview: true);
        Assert.Equal("Önizleme", preview.Status);
        Assert.Equal(1, preview.Created);
        Assert.Null(Emp(domain, ayse.Id)); // önizleme hiçbir şey yazmaz

        var run = await Sync(admin);
        Assert.Equal("Başarılı", run.Status);
        Assert.Equal(1, run.Created);
        Assert.Equal(1, run.Unmatched);
        Assert.Contains(run.Changes, c => c.Kind == "Eşleşmeyen" && c.Name == "Yabancı Kişi");
        var e = Emp(domain, ayse.Id)!;
        Assert.Equal("Ayşe Test", e.FullName);
        Assert.Equal(EmployeeSource.Directory, e.Source);
        Assert.Equal("ayse.test", e.DirectoryUserName);

        // Ad ve kullanıcı adı değişir; kalıcı kimlik aynı kaldığı için yeni kayıt oluşmaz.
        dir.Add(ayse with { DisplayName = "Ayşe Test Yılmaz", UserName = "ayse.yilmaz" });
        run = await Sync(admin);
        Assert.Equal(0, run.Created);
        Assert.Equal(1, app.Db(db => db.Employees.Count(x => x.DirectoryId == domain + ":" + ayse.Id)));
        Assert.Equal("Ayşe Test Yılmaz", Emp(domain, ayse.Id)!.FullName);
        Assert.Equal("ayse.yilmaz", Emp(domain, ayse.Id)!.DirectoryUserName);
    }

    [Fact]
    public async Task Failed_or_suspicious_sync_does_not_mass_deactivate()
    {
        var (f, dir, domain, co) = Setup();
        await using var _ = f;
        var admin = await Admin(f, co);
        var people = Enumerable.Range(1, 6).Select(i => P($"kisi{i}", $"Kişi {i}", co)).ToList();
        foreach (var p in people) dir.Add(p);
        Assert.Equal(6, (await Sync(admin)).Created);

        // Dizin kapalı: çalışma başarısız, kimse pasife alınmaz.
        dir.Down = true;
        var failed = await Sync(admin);
        Assert.Equal("Başarısız", failed.Status);
        Assert.All(people, p => Assert.True(Emp(domain, p.Id)!.IsActive));
        dir.Down = false;

        // Arama beklenenden çok az kayıt döndürür (ör. yanlış OU): toplu pasife alma yapılmaz.
        foreach (var p in people.Skip(1)) dir.People.TryRemove(p.Id, out var _);
        var suspicious = await Sync(admin);
        Assert.Equal(0, suspicious.Deactivated);
        Assert.NotNull(suspicious.Message);
        Assert.All(people, p => Assert.True(Emp(domain, p.Id)!.IsActive));

        // Gerçekten ayrılan tek kişi: pasife alınır, geçmiş korunur.
        foreach (var p in people.Skip(1)) dir.Add(p);
        dir.People.TryRemove(people[5].Id, out var _);
        var normal = await Sync(admin);
        Assert.Equal(1, normal.Deactivated);
        Assert.False(Emp(domain, people[5].Id)!.IsActive);
        Assert.NotNull(Emp(domain, people[5].Id)); // silinmez
    }

    [Fact]
    public async Task Ad_login_checks_directory_without_local_fallback_and_disable_revokes_sessions()
    {
        var (f, dir, domain, co) = Setup();
        await using var _ = f;
        var admin = await Admin(f, co);
        var user = "can.ad" + RandomNumberGenerator.GetHexString(3).ToLowerInvariant();
        var can = P(user, "Can AD", co);
        dir.Add(can, "Dogru-Parola-1");
        await Sync(admin);
        var emp = Emp(domain, can.Id)!;

        // AD'den gelen personelin otomatik giriş hakkı yok.
        var anon = f.CreateClient();
        anon.DefaultRequestHeaders.Add("X-NE-Request", "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostJson("/api/auth/login", new LoginRequest(user, "Dogru-Parola-1", "Directory"))).StatusCode);

        var nrl = app.Db(db => db.Companies.First(x => x.Code == "NRL").Id);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJson("/api/users",
            new UserCreate("", "", null, Roles.Auditor, [new ScopeSave(nrl, null)], "Directory", emp.Id))).StatusCode);

        // Yanlış parola reddedilir; yerel parola doğrulamasına düşülmez.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostJson("/api/auth/login", new LoginRequest(user, "Yanlis", "Directory"))).StatusCode);
        // Hesap türü uyuşmazsa reddedilir.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostJson("/api/auth/login", new LoginRequest(user, "Dogru-Parola-1", "Local"))).StatusCode);

        // NUROL\kullanici biçimi de kabul edilir.
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-NE-Request", "1");
        var ok = await client.PostJson("/api/auth/login", new LoginRequest("NUROL\\" + user.ToUpperInvariant(), "Dogru-Parola-1", "Directory"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Directory", (await ok.Read<MeDto>()).AccountType);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        // Dizine ulaşılamıyor: 503, yerel parolaya düşülmez.
        dir.Down = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await anon.PostJson("/api/auth/login", new LoginRequest(user, "Dogru-Parola-1", "Directory"))).StatusCode);
        dir.Down = false;

        // AD'de devre dışı: giriş reddedilir; eşitleme uygulama hesabını kapatır ve açık oturumu iptal eder.
        dir.Add(can with { Disabled = true });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostJson("/api/auth/login", new LoginRequest(user, "Dogru-Parola-1", "Directory"))).StatusCode);
        var run = await Sync(admin);
        Assert.Contains(run.Changes, c => c.Kind == "Hesap kapatıldı");
        Assert.False(app.Db(db => db.Users.First(u => u.EmployeeId == emp.Id).IsActive));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Same_username_with_different_directory_identity_cannot_take_over_account()
    {
        var (f, dir, domain, co) = Setup();
        await using var _ = f;
        var admin = await Admin(f, co);
        var user = "devir.test" + RandomNumberGenerator.GetHexString(3).ToLowerInvariant();
        var original = P(user, "Asıl Kişi", co);
        dir.Add(original, "Parola-1");
        await Sync(admin);
        var nrl = app.Db(db => db.Companies.First(x => x.Code == "NRL").Id);
        await admin.PostJson("/api/users", new UserCreate("", "", null, Roles.Auditor, [new ScopeSave(nrl, null)], "Directory", Emp(domain, original.Id)!.Id));

        // Asıl hesap silinip aynı kullanıcı adıyla yeni bir AD hesabı açılır.
        dir.People.TryRemove(original.Id, out var _);
        dir.Add(P(user, "Yeni Kişi", co), "Parola-2");
        var anon = f.CreateClient();
        anon.DefaultRequestHeaders.Add("X-NE-Request", "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostJson("/api/auth/login", new LoginRequest(user, "Parola-2", "Directory"))).StatusCode);
    }

    [Fact]
    public async Task Rule_resolution_uses_priority_and_ou()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var rules = new[]
        {
            new DirectoryCompanyRule { Priority = 1, Attribute = "dn", MatchType = DirectoryMatch.DnEndsWith, Value = "OU=Makina, DC=nurol, DC=local", CompanyId = b },
            new DirectoryCompanyRule { Priority = 5, Attribute = "company", MatchType = DirectoryMatch.Contains, Value = "nurol", CompanyId = a },
        };
        var inMakinaOu = new DirectoryPerson("1", "CN=X,OU=Makina,DC=nurol,DC=local", "x", "X", null, null, null, "Nurol İnşaat", null, false);
        var other = inMakinaOu with { Dn = "CN=Y,OU=Insaat,DC=nurol,DC=local" };
        var none = other with { Company = "Başka" };
        Assert.Equal(b, Nurol.Envanter.Server.Services.DirectorySync.Resolve(rules, inMakinaOu));
        Assert.Equal(a, Nurol.Envanter.Server.Services.DirectorySync.Resolve(rules, other));
        Assert.Null(Nurol.Envanter.Server.Services.DirectorySync.Resolve(rules, none));
        await Task.CompletedTask;
    }

    [Fact]
    public void Ldap_filter_values_are_escaped_and_logins_normalized()
    {
        Assert.Equal(@"a\2a\28\29\5c", LdapDirectoryClient.Escape(@"a*()\"));
        Assert.Equal("ayse.korkmaz", LdapDirectoryClient.NormalizeLogin(@"NUROL\Ayse.Korkmaz"));
        Assert.Equal("ayse.korkmaz", LdapDirectoryClient.NormalizeLogin("ayse.korkmaz@nurol.local"));
    }
}

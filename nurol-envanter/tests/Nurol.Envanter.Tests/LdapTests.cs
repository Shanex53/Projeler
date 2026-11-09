using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Nurol.Envanter.Server.Infrastructure;

namespace Nurol.Envanter.Tests;

/// <summary>
/// LDAP istemcisinin gerçek bir dizin sunucusuna (OpenLDAP) karşı testi: sayfalı arama, kullanıcı bulma,
/// parola doğrulama ve öznitelik eşlemesi. Windows AD'ye özgü öznitelikler (objectGUID, sAMAccountName,
/// userAccountControl) burada OpenLDAP karşılıklarıyla (entryUUID, uid) eşlenir; gerçek AD ile uçtan uca
/// bağlantı kurum ağında doğrulanmalıdır.
/// </summary>
public class LdapTests : IAsyncLifetime
{
    IContainer ldap = default!;
    DirectoryOptions options = default!;

    const string Ldif = """
        dn: ou=Personel,dc=nurol,dc=local
        objectClass: organizationalUnit
        ou: Personel

        dn: uid=ayse.korkmaz,ou=Personel,dc=nurol,dc=local
        objectClass: inetOrgPerson
        uid: ayse.korkmaz
        cn: Ayşe Korkmaz
        sn: Korkmaz
        givenName: Ayşe
        mail: ayse.korkmaz@nurol.com.tr
        title: Satın Alma Uzmanı
        departmentNumber: Satın Alma Müdürlüğü
        o: Nurol İnşaat
        employeeNumber: 10482
        userPassword: Ayse-Parola-1

        dn: uid=mehmet.yildirim,ou=Personel,dc=nurol,dc=local
        objectClass: inetOrgPerson
        uid: mehmet.yildirim
        cn: Mehmet Yıldırım
        sn: Yıldırım
        o: Nurol İnşaat
        userPassword: Mehmet-Parola-1

        dn: uid=ozel(karakter),ou=Personel,dc=nurol,dc=local
        objectClass: inetOrgPerson
        uid: ozel(karakter)
        cn: Özel Karakter
        sn: Karakter
        userPassword: Ozel-Parola-1
        """;

    public async Task InitializeAsync()
    {
        ldap = new ContainerBuilder()
            .WithImage("osixia/openldap:1.5.0")
            .WithEnvironment("LDAP_DOMAIN", "nurol.local")
            .WithEnvironment("LDAP_ADMIN_PASSWORD", "admin-test")
            .WithEnvironment("LDAP_TLS", "false")
            .WithPortBinding(389, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("slapd starting"))
            .Build();
        await ldap.StartAsync();
        await ldap.CopyAsync(Encoding.UTF8.GetBytes(Ldif), "/tmp/kisiler.ldif");
        for (var i = 0; ; i++)
        {
            var r = await ldap.ExecAsync(["ldapadd", "-x", "-H", "ldap://localhost", "-D", "cn=admin,dc=nurol,dc=local", "-w", "admin-test", "-f", "/tmp/kisiler.ldif"]);
            if (r.ExitCode == 0) break;
            if (i > 10) throw new InvalidOperationException("LDIF yüklenemedi: " + r.Stderr);
            await Task.Delay(1000);
        }
        options = new DirectoryOptions
        {
            Server = ldap.Hostname, Port = ldap.GetMappedPublicPort(389), UseSsl = false, BaseDn = "ou=Personel,dc=nurol,dc=local",
            Filter = "(objectClass=inetOrgPerson)", BindDn = "cn=admin,dc=nurol,dc=local", BindPassword = "admin-test",
            IdAttribute = "entryUUID", IdIsBinaryGuid = false, UserNameAttribute = "uid", DisplayNameAttribute = "cn",
            CompanyAttribute = "o", DepartmentAttribute = "departmentNumber", EmployeeNumberAttribute = "employeeNumber",
            DisabledMode = "None", PageSize = 1 // sayfalama döngüsünü sınar
        };
    }

    public async Task DisposeAsync() => await ldap.DisposeAsync();

    LdapDirectoryClient Client(DirectoryOptions? o = null) => new(o ?? options, NullLogger<LdapDirectoryClient>.Instance);

    [Fact]
    public async Task Paged_search_returns_all_people_with_mapped_attributes()
    {
        var people = await Client().SearchAllAsync();
        Assert.Equal(3, people.Count);
        var ayse = people.Single(p => p.UserName == "ayse.korkmaz");
        Assert.Equal("Ayşe Korkmaz", ayse.DisplayName);
        Assert.Equal("Nurol İnşaat", ayse.Company);
        Assert.Equal("Satın Alma Müdürlüğü", ayse.Department);
        Assert.Equal("10482", ayse.EmployeeNumber);
        Assert.True(Guid.TryParse(ayse.Id, out _));
        Assert.EndsWith("ou=Personel,dc=nurol,dc=local", ayse.Dn, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Find_and_verify_password()
    {
        var c = Client();
        var p = await c.FindUserAsync("mehmet.yildirim");
        Assert.NotNull(p);
        Assert.True(await c.VerifyPasswordAsync(p.Dn, "Mehmet-Parola-1"));
        Assert.False(await c.VerifyPasswordAsync(p.Dn, "yanlis"));
        // Boş parola anonim bağlantı gibi kabul edilmemeli.
        Assert.False(await c.VerifyPasswordAsync(p.Dn, ""));
        Assert.Null(await c.FindUserAsync("olmayan.kisi"));
        // Filtre enjeksiyonu: '*' joker karakter olarak yorumlanmaz.
        Assert.Null(await c.FindUserAsync("*"));
        Assert.NotNull(await c.FindUserAsync("ozel(karakter)"));
    }

    [Fact]
    public async Task Wrong_service_password_and_unreachable_server_raise_clear_errors()
    {
        var bad = Client(new DirectoryOptions
        {
            Server = options.Server, Port = options.Port, UseSsl = false, BaseDn = options.BaseDn, Filter = options.Filter,
            BindDn = options.BindDn, BindPassword = "yanlis", IdAttribute = "entryUUID", IdIsBinaryGuid = false, UserNameAttribute = "uid", DisabledMode = "None"
        });
        var ex = await Assert.ThrowsAsync<DirectoryUnavailableException>(() => bad.SearchAllAsync());
        Assert.Contains("servis hesabı", ex.Message);

        var down = Client(new DirectoryOptions { Server = "127.0.0.1", Port = 1, UseSsl = false, BaseDn = "dc=x", TimeoutSeconds = 3 });
        await Assert.ThrowsAsync<DirectoryUnavailableException>(() => down.SearchAllAsync());
    }
}

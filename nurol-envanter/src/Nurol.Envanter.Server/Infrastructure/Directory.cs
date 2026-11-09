using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Infrastructure;

/// <summary>
/// Active Directory bağlantı ayarları. Sunucu, arama kökü ve öznitelik eşlemesi values/ConfigMap ile;
/// servis hesabı parolası (BindPassword) yalnızca Secret ile verilir. Parolalar veritabanına yazılmaz.
/// </summary>
public class DirectoryOptions
{
    /// <summary>Personel eşitlemesi açık mı.</summary>
    public bool Enabled { get; set; }
    /// <summary>AD hesaplarıyla giriş açık mı. Eşitlemeden bağımsızdır.</summary>
    public bool LoginEnabled { get; set; }
    public string? Server { get; set; }
    public int Port { get; set; } = 636;
    public bool UseSsl { get; set; } = true;
    public string? BaseDn { get; set; }
    public string Filter { get; set; } = "(&(objectCategory=person)(objectClass=user))";
    public string? BindDn { get; set; }
    public string? BindPassword { get; set; }
    /// <summary>Kalıcı kimliğin ön eki (ör. nurol.local). Aynı GUID farklı alan adlarında çakışmaz.</summary>
    public string Domain { get; set; } = "ad";
    public string IdAttribute { get; set; } = "objectGUID";
    /// <summary>objectGUID ikili (16 bayt) gelir; OpenLDAP entryUUID gibi metin kimlikler için false.</summary>
    public bool IdIsBinaryGuid { get; set; } = true;
    public string UserNameAttribute { get; set; } = "sAMAccountName";
    public string DisplayNameAttribute { get; set; } = "displayName";
    public string EmailAttribute { get; set; } = "mail";
    public string TitleAttribute { get; set; } = "title";
    public string DepartmentAttribute { get; set; } = "department";
    public string CompanyAttribute { get; set; } = "company";
    public string EmployeeNumberAttribute { get; set; } = "employeeID";
    /// <summary>"UserAccountControl" (AD devre dışı biti) veya "None".</summary>
    public string DisabledMode { get; set; } = "UserAccountControl";
    public int PageSize { get; set; } = 500;
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Bulunan kayıt sayısı aktif AD personelinin bu oranının altındaysa toplu pasife alma yapılmaz.</summary>
    public double MinFoundRatio { get; set; } = 0.5;
    /// <summary>Ekranda gösterilecek zamanlama bilgisi (ör. "Her gece 02:00"). CronJob Helm ile ayarlanır.</summary>
    public string? ScheduleNote { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(BaseDn);

    public string[] Attributes => new[] { IdAttribute, UserNameAttribute, DisplayNameAttribute, EmailAttribute, TitleAttribute, DepartmentAttribute,
        CompanyAttribute, EmployeeNumberAttribute, "givenName", "sn", "cn", DisabledMode == "UserAccountControl" ? "userAccountControl" : null }
        .Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()!;
}

public record DirectoryPerson(
    string Id, string Dn, string UserName, string DisplayName, string? Email, string? Title, string? Department,
    string? Company, string? EmployeeNumber, bool Disabled)
{
    public string? Attribute(string name) => name.ToLowerInvariant() switch
    {
        "company" => Company,
        "department" => Department,
        "title" => Title,
        "mail" => Email,
        "dn" or "distinguishedname" => Dn,
        "employeeid" or "employeenumber" => EmployeeNumber,
        _ => null
    };
}

public interface IDirectoryClient
{
    Task<IReadOnlyList<DirectoryPerson>> SearchAllAsync(CancellationToken ct = default);
    Task<DirectoryPerson?> FindUserAsync(string userName, CancellationToken ct = default);
    /// <summary>Kullanıcının kendi parolasıyla AD'ye bağlanmayı dener. Parola veritabanına alınmaz.</summary>
    Task<bool> VerifyPasswordAsync(string dn, string password, CancellationToken ct = default);
}

/// <summary>Dizine ulaşılamadığında (ağ, TLS, servis hesabı) fırlatılır. Giriş yerel parolaya düşmez.</summary>
public class DirectoryUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public class LdapDirectoryClient(DirectoryOptions o, ILogger<LdapDirectoryClient> log) : IDirectoryClient
{
    LdapConnection Connect(NetworkCredential cred)
    {
        if (!o.Configured) throw new DirectoryUnavailableException("Active Directory bağlantısı yapılandırılmamış.");
        var c = new LdapConnection(new LdapDirectoryIdentifier(o.Server, o.Port), cred, AuthType.Basic);
        c.SessionOptions.ProtocolVersion = 3;
        c.SessionOptions.SecureSocketLayer = o.UseSsl;
        c.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        c.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        c.Bind();
        return c;
    }

    LdapConnection ServiceConnection()
    {
        try { return Connect(new NetworkCredential(o.BindDn, o.BindPassword)); }
        catch (LdapException ex)
        {
            log.LogError(ex, "AD servis hesabıyla bağlanılamadı ({Code})", ex.ErrorCode);
            throw new DirectoryUnavailableException(ex.ErrorCode == 49
                ? "AD servis hesabı bilgileri reddedildi. Secret'taki kullanıcı ve parolayı kontrol edin."
                : $"Active Directory sunucusuna ulaşılamadı ({o.Server}:{o.Port}). Ağ erişimini ve kurum CA sertifikasını kontrol edin.", ex);
        }
    }

    public Task<IReadOnlyList<DirectoryPerson>> SearchAllAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = ServiceConnection();
        return (IReadOnlyList<DirectoryPerson>)Search(c, o.Filter, ct);
    }, ct);

    public Task<DirectoryPerson?> FindUserAsync(string userName, CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = ServiceConnection();
        var filter = $"(&{o.Filter}({o.UserNameAttribute}={Escape(userName)}))";
        var found = Search(c, filter, ct);
        return found.Count == 1 ? found[0] : null;
    }, ct);

    public Task<bool> VerifyPasswordAsync(string dn, string password, CancellationToken ct = default) => Task.Run(() =>
    {
        // Boş parola anonim bağlantı gibi kabul edilebilir; asla doğrulama sayılmaz.
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(dn)) return false;
        try
        {
            using var c = Connect(new NetworkCredential(dn, password));
            return true;
        }
        catch (LdapException ex) when (ex.ErrorCode == 49) { return false; }
        catch (LdapException ex)
        {
            throw new DirectoryUnavailableException("Active Directory sunucusuna ulaşılamadı.", ex);
        }
    }, ct);

    List<DirectoryPerson> Search(LdapConnection c, string filter, CancellationToken ct)
    {
        var result = new List<DirectoryPerson>();
        var req = new SearchRequest(o.BaseDn, filter, SearchScope.Subtree, o.Attributes);
        var page = new PageResultRequestControl(o.PageSize);
        req.Controls.Add(page);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            SearchResponse res;
            try { res = (SearchResponse)c.SendRequest(req); }
            catch (DirectoryException ex) { throw new DirectoryUnavailableException("AD araması tamamlanamadı: " + ex.Message, ex); }
            foreach (SearchResultEntry e in res.Entries)
                if (Map(e) is { } p) result.Add(p);
            var cookie = res.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie is null || cookie.Length == 0) break;
            page.Cookie = cookie;
        }
        return result;
    }

    DirectoryPerson? Map(SearchResultEntry e)
    {
        string? S(string attr) => e.Attributes.Contains(attr) && e.Attributes[attr].Count > 0 ? e.Attributes[attr][0] switch
        {
            string s => s,
            byte[] b => Encoding.UTF8.GetString(b),
            var x => x?.ToString()
        } : null;

        string? id;
        if (o.IdIsBinaryGuid)
        {
            var raw = e.Attributes.Contains(o.IdAttribute) ? e.Attributes[o.IdAttribute].GetValues(typeof(byte[])).OfType<byte[]>().FirstOrDefault() : null;
            id = raw is { Length: 16 } ? new Guid(raw).ToString() : null;
        }
        else id = S(o.IdAttribute);
        var userName = S(o.UserNameAttribute);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(userName)) return null;

        var display = S(o.DisplayNameAttribute) ?? string.Join(' ', new[] { S("givenName"), S("sn") }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(display)) display = S("cn") ?? userName;
        var disabled = o.DisabledMode == "UserAccountControl" && int.TryParse(S("userAccountControl"), out var uac) && (uac & 2) != 0;
        return new DirectoryPerson(id.ToLowerInvariant(), e.DistinguishedName, userName, display.Trim(), S(o.EmailAttribute), S(o.TitleAttribute),
            S(o.DepartmentAttribute), S(o.CompanyAttribute), S(o.EmployeeNumberAttribute), disabled);
    }

    /// <summary>LDAP arama filtresi kaçışı (RFC 4515).</summary>
    public static string Escape(string v)
    {
        var sb = new StringBuilder();
        foreach (var ch in v)
            sb.Append(ch switch { '\\' => @"\5c", '*' => @"\2a", '(' => @"\28", ')' => @"\29", '\0' => @"\00", _ => ch.ToString() });
        return sb.ToString();
    }

    /// <summary>"NUROL\ayse.korkmaz" veya "ayse.korkmaz@nurol.local" girişini kullanıcı adına indirger.</summary>
    public static string NormalizeLogin(string input)
    {
        var s = input.Trim();
        var slash = s.LastIndexOf('\\');
        if (slash >= 0) s = s[(slash + 1)..];
        var at = s.IndexOf('@');
        if (at > 0) s = s[..at];
        return s.ToLowerInvariant();
    }
}

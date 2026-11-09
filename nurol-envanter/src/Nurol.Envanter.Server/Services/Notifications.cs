using System.Net;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Services;

public class SmtpOptions
{
    public bool Enabled { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    /// <summary>"StartTls", "SslOnConnect" veya "None".</summary>
    public string Security { get; set; } = "StartTls";
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }
    public string FromName { get; set; } = "Nurol Envanter";
    /// <summary>E-postalardaki bağlantılar için uygulamanın dış adresi (ör. https://envanter.kurum.local).</summary>
    public string? BaseUrl { get; set; }
    public int PollSeconds { get; set; } = 30;
    /// <summary>Günlük özette imzalı evrakı bu kadar günden eski tutanaklar listelenir.</summary>
    public int MissingDocDays { get; set; } = 3;
    public int MaxAttempts { get; set; } = 8;
}

/// <summary>Bildirim üretimi. Kayıtlar outbox'a yazılır; SaveChanges çağırana aittir.</summary>
public class Notifier(AppDbContext db, SmtpOptions o)
{
    record Recipient(string UserId, string Email, string Name);

    /// <summary>Bir depoda yazma yetkisi olan, e-posta bildirimi açık kullanıcılar (sistem yöneticileri hariç).</summary>
    async Task<List<Recipient>> WarehouseRecipients(Guid warehouseId)
    {
        var companyId = await db.Warehouses.Where(w => w.Id == warehouseId).Select(w => w.CompanyId).FirstAsync();
        var writeRoles = new[] { Roles.CompanyAdmin, Roles.WarehouseManager };
        return await (from u in db.Users
                      where u.IsActive && u.NotifyByEmail && u.Email != null && u.Email != ""
                      where db.UserRoles.Any(ur => ur.UserId == u.Id && db.Roles.Any(r => r.Id == ur.RoleId && writeRoles.Contains(r.Name!)))
                      where db.UserScopes.Any(s => s.UserId == u.Id && (s.WarehouseId == warehouseId || (s.WarehouseId == null && s.CompanyId == companyId)))
                      select new Recipient(u.Id, u.Email!, u.DisplayName)).ToListAsync();
    }

    async Task<List<Recipient>> Admins() =>
        await (from u in db.Users
               where u.IsActive && u.NotifyByEmail && u.Email != null && u.Email != ""
               where db.UserRoles.Any(ur => ur.UserId == u.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == Roles.SystemAdmin))
               select new Recipient(u.Id, u.Email!, u.DisplayName)).ToListAsync();

    void Add(string kind, Recipient to, string subject, string body, string dedup)
    {
        db.OutboxMails.Add(new OutboxMail
        {
            Id = Guid.NewGuid(), Kind = kind, ToEmail = to.Email, ToName = to.Name, Subject = subject, BodyHtml = body,
            DedupKey = dedup, CreatedAt = DateTimeOffset.UtcNow, SendAfter = DateTimeOffset.UtcNow
        });
    }

    public async Task TransferSentAsync(Transfer t, string sourceName, string targetName, int items, int stockLines)
    {
        foreach (var r in await WarehouseRecipients(t.TargetWarehouseId))
        {
            var parts = string.Join(" ve ", new[] { items > 0 ? $"{items} cihaz" : null, stockLines > 0 ? $"{stockLines} kalem malzeme" : null }.Where(x => x != null));
            Add("Transfer", r, $"{t.No}: {sourceName} deposundan transfer yolda",
                Mail.Html($"Merhaba {r.Name},", [
                    $"<b>{Mail.E(sourceName)}</b> deposundan <b>{Mail.E(targetName)}</b> deposuna {parts} gönderildi.",
                    "Kalemler size ulaştığında uygulamada teslim alın. Kısmi teslim yoktur; eksik varsa teslim almadan kaynak depoyla görüşün."
                ], ("Transferi aç", Link($"transferler/{t.Id}"))),
                $"transfer-sent:{t.Id}:{r.UserId}");
        }
    }

    public async Task DirectoryOffboardingAsync(DirectorySyncRun run, IReadOnlyList<(string Name, int OpenItems)> people)
    {
        if (people.Count == 0) return;
        foreach (var r in await Admins())
            Add("AD", r, $"AD eşitlemesi: açık zimmeti olan {people.Count} personel pasife alındı",
                Mail.Html($"Merhaba {r.Name},", [
                    "Son Active Directory eşitlemesinde aşağıdaki personel pasife alındı ve üzerinde açık zimmet var:",
                    Mail.List(people.Select(p => $"{p.Name} · {p.OpenItems} cihaz")),
                    "İade sürecini başlatın."
                ], ("Ayrılan personel raporu", Link("raporlar?r=ayrilan"))),
                $"ad-offboard:{run.Id}:{r.UserId}");
    }

    /// <summary>
    /// Günlük özet: kullanıcının kapsamındaki eksik imzalı evrak, teslim alınmayı bekleyen transfer, kritik stok ve
    /// açık zimmeti kalan pasif personel. Bildirecek bir şey yoksa e-posta üretilmez. Aynı gün ikinci kez çalışırsa yeni e-posta üretmez.
    /// </summary>
    public async Task<int> DailyDigestAsync(DateOnly day, CancellationToken ct = default)
    {
        var users = await db.Users.Where(u => u.IsActive && u.NotifyByEmail && u.Email != null && u.Email != "").Select(u => new { u.Id, u.Email, u.DisplayName }).ToListAsync(ct);
        var already = (await db.OutboxMails.Where(m => m.DedupKey != null && m.DedupKey.StartsWith($"digest:{day:yyyy-MM-dd}:")).Select(m => m.DedupKey!).ToListAsync(ct)).ToHashSet();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-o.MissingDocDays);
        var count = 0;
        foreach (var u in users)
        {
            var key = $"digest:{day:yyyy-MM-dd}:{u.Id}";
            if (already.Contains(key)) continue;
            Access a;
            try { a = await Access.ForUser(db, u.Id, ct); } catch (AppException) { continue; }
            if (!a.IsSystemAdmin && a.Warehouses.Count == 0) continue;

            var missing = await a.Assignments(db).Where(x => x.At < cutoff && !x.Documents.Any(d => d.Kind == DocumentKind.Signed))
                .OrderBy(x => x.At).Select(x => new { x.No, x.At, x.Employee!.FullName }).Take(11).ToListAsync(ct);
            var missingTotal = missing.Count > 10 ? await a.Assignments(db).CountAsync(x => x.At < cutoff && !x.Documents.Any(d => d.Kind == DocumentKind.Signed), ct) : missing.Count;
            var waiting = a.CanWrite
                ? await db.Transfers.Where(t => t.Status == TransferStatus.Sent && (a.IsSystemAdmin || a.Warehouses.Contains(t.TargetWarehouseId)))
                    .Select(t => new { t.No, Src = t.SourceWarehouse!.Name, Tgt = t.TargetWarehouse!.Name }).Take(10).ToListAsync(ct)
                : [];
            var stock = await a.StockItems(db).Where(s => !s.Archived && s.MinLevel > 0).ToListAsync(ct);
            var stockIds = stock.Select(s => s.Id).ToList();
            var whs = await a.WarehousesQuery(db).Where(w => !w.Archived).Select(w => new { w.Id, w.Name, w.CompanyId }).ToListAsync(ct);
            var bal = await db.StockBalances.Where(b => stockIds.Contains(b.StockItemId)).ToListAsync(ct);
            var low = stock.SelectMany(s => whs.Where(w => w.CompanyId == s.CompanyId)
                    .Select(w => (s, w.Name, Q: bal.FirstOrDefault(b => b.StockItemId == s.Id && b.WarehouseId == w.Id)?.Quantity ?? 0)))
                .Where(x => x.Q < x.s.MinLevel).Take(10).ToList();
            var leavers = await a.Items(db).Where(i => i.EmployeeId != null && !i.Employee!.IsActive)
                .GroupBy(i => i.Employee!.FullName).Select(g => new { Name = g.Key, N = g.Count() }).Take(10).ToListAsync(ct);

            if (missing.Count == 0 && waiting.Count == 0 && low.Count == 0 && leavers.Count == 0) continue;
            var blocks = new List<string>();
            if (waiting.Count > 0)
                blocks.Add($"<h3 style=\"margin:18px 0 6px\">Teslim almanızı bekleyen transfer ({waiting.Count})</h3>" +
                           Mail.List(waiting.Select(t => $"{t.No} · {t.Src} → {t.Tgt}")));
            if (missing.Count > 0)
                blocks.Add($"<h3 style=\"margin:18px 0 6px\">İmzalı evrakı {o.MissingDocDays} günden uzun süredir eksik ({missingTotal})</h3>" +
                           Mail.List(missing.Take(10).Select(m => $"{m.No} · {m.FullName} · {TimeZones.Format(m.At)[..10]}")));
            if (low.Count > 0)
                blocks.Add($"<h3 style=\"margin:18px 0 6px\">Kritik stok ({low.Count})</h3>" +
                           Mail.List(low.Select(x => $"{x.s.Name} · {x.Name}: {x.Q:0.###} {x.s.Unit} (en az {x.s.MinLevel:0.###})")));
            if (leavers.Count > 0)
                blocks.Add($"<h3 style=\"margin:18px 0 6px\">Açık zimmeti kalan pasif personel ({leavers.Count})</h3>" +
                           Mail.List(leavers.Select(l => $"{l.Name} · {l.N} cihaz")));

            Add("Günlük özet", new Recipient(u.Id, u.Email!, u.DisplayName), $"Nurol Envanter günlük özet · {day:dd.MM.yyyy}",
                Mail.Html($"Merhaba {u.DisplayName},", ["Kapsamınızdaki depolarda ilgilenmeniz gereken kayıtlar:", .. blocks], ("Uygulamayı aç", Link(""))), key);
            count++;
        }
        await db.SaveChangesAsync(ct);
        return count;
    }

    public void Test(string email, string name) =>
        Add("Test", new Recipient("", email, name), "Nurol Envanter test e-postası",
            Mail.Html($"Merhaba {name},", ["Bu bir test e-postasıdır. Bildirim ayarları doğru çalışıyor."], ("Uygulamayı aç", Link(""))), $"test:{Guid.NewGuid()}");

    string? Link(string path) => string.IsNullOrWhiteSpace(o.BaseUrl) ? null : o.BaseUrl.TrimEnd('/') + "/" + path;
}

public static class Mail
{
    public static string E(string s) => WebUtility.HtmlEncode(s);

    public static string List(IEnumerable<string> items) =>
        "<ul style=\"margin:0;padding-left:18px\">" + string.Concat(items.Select(i => $"<li style=\"margin:3px 0\">{E(i)}</li>")) + "</ul>";

    /// <summary>Basit, istemcilerde güvenli görünen HTML. Paragraf öğeleri HTML (List/E ile güvenli üretilmiş) ya da düz metin olabilir.</summary>
    public static string Html(string greeting, IEnumerable<string> paragraphs, (string Text, string? Url) button)
    {
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#142235;max-width:620px\">");
        sb.Append("<div style=\"background:#0f2744;color:#fff;padding:14px 18px;border-radius:8px 8px 0 0;font-weight:700\">Nurol Envanter</div>");
        sb.Append("<div style=\"border:1px solid #dbe2ec;border-top:0;padding:18px;border-radius:0 0 8px 8px\">");
        sb.Append($"<p style=\"margin:0 0 12px\">{E(greeting)}</p>");
        foreach (var p in paragraphs)
            sb.Append(p.StartsWith('<') ? p : $"<p style=\"margin:0 0 10px\">{(p.Contains("<b>") ? p : E(p))}</p>");
        if (button.Url is not null)
            sb.Append($"<p style=\"margin:18px 0 0\"><a href=\"{E(button.Url)}\" style=\"background:#1f5fd1;color:#fff;padding:9px 16px;border-radius:6px;text-decoration:none;font-weight:600\">{E(button.Text)}</a></p>");
        sb.Append("<p style=\"margin:18px 0 0;color:#5a6a80;font-size:12px\">Bu e-posta otomatik gönderilmiştir. Bildirim tercihinizi uygulamada “Hesabım” sayfasından değiştirebilirsiniz.</p>");
        sb.Append("</div></div>");
        return sb.ToString();
    }
}

/// <summary>
/// Outbox gönderici. Çoklu pod'da aynı satırı iki pod göndermez (FOR UPDATE SKIP LOCKED).
/// Başarısız gönderimler artan beklemeyle yeniden denenir.
/// </summary>
public class MailSender(IServiceScopeFactory scopes, SmtpOptions o, ILogger<MailSender> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!o.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, o.PollSeconds)));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await SendBatchAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), o, log, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "E-posta gönderimi başarısız");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public static async Task<int> SendBatchAsync(AppDbContext db, SmtpOptions o, ILogger log, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var batch = await db.OutboxMails.FromSql($"""
            SELECT * FROM outbox_mails
            WHERE sent_at IS NULL AND attempts < {o.MaxAttempts} AND send_after <= now()
            ORDER BY created_at LIMIT 20 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        if (batch.Count == 0) return 0;
        using var smtp = new SmtpClient();
        var sent = 0;
        try
        {
            var sec = o.Security switch { "SslOnConnect" => SecureSocketOptions.SslOnConnect, "None" => SecureSocketOptions.None, _ => SecureSocketOptions.StartTls };
            await smtp.ConnectAsync(o.Host, o.Port, sec, ct);
            if (!string.IsNullOrEmpty(o.UserName)) await smtp.AuthenticateAsync(o.UserName, o.Password, ct);
            foreach (var m in batch)
            {
                try
                {
                    var msg = new MimeMessage();
                    msg.From.Add(new MailboxAddress(o.FromName, o.From));
                    msg.To.Add(new MailboxAddress(m.ToName, m.ToEmail));
                    msg.Subject = m.Subject;
                    msg.Body = new BodyBuilder { HtmlBody = m.BodyHtml }.ToMessageBody();
                    await smtp.SendAsync(msg, ct);
                    m.SentAt = DateTimeOffset.UtcNow; m.LastError = null;
                    sent++;
                }
                catch (Exception ex) when (ex is SmtpCommandException or ParseException or FormatException)
                {
                    Fail(m, ex.Message);
                }
            }
            await smtp.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("SMTP sunucusuna bağlanılamadı: {Message}", ex.Message);
            foreach (var m in batch.Where(m => m.SentAt is null)) Fail(m, "SMTP bağlantısı: " + ex.Message);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return sent;
    }

    static void Fail(OutboxMail m, string error)
    {
        m.Attempts++;
        m.LastError = error.Length > 500 ? error[..500] : error;
        m.SendAfter = DateTimeOffset.UtcNow.AddMinutes(Math.Pow(2, m.Attempts));
    }
}

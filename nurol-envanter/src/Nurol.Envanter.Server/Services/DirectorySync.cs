using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Services;

/// <summary>
/// AD → personel eşitlemesi. Kurallar:
/// - Kalıcı kimlik alan adı + objectGUID; ad veya kullanıcı adı değişmesi yeni kayıt oluşturmaz.
/// - Şirket, yöneticinin tanımladığı eşleme kurallarıyla belirlenir; eşleşmeyen yeni kişi oluşturulmaz, raporlanır.
/// - Arama tamamlanamazsa hiçbir kayıt değişmez. Bulunan kayıt sayısı şüpheli derecede azsa toplu pasife alma yapılmaz.
/// - Pasife alınan personelin geçmişi korunur; açık zimmeti varsa raporda belirtilir. Bağlı uygulama hesabı kapatılır, oturumları iptal edilir.
/// - Aynı anda tek eşitleme çalışır (PostgreSQL advisory lock).
/// </summary>
public class DirectorySync(AppDbContext db, IDirectoryClient dir, DirectoryOptions o, Notifier notifier, OffboardingService offboarding, ILogger<DirectorySync> log)
{
    const long LockKey = 472020;

    public async Task<DirectorySyncRun> RunAsync(bool preview, string trigger, string userName, CancellationToken ct = default)
    {
        if (!o.Configured) throw AppException.Conflict("Active Directory bağlantısı yapılandırılmamış (Directory:Server ve Directory:BaseDn).");
        if (!preview && !o.Enabled) throw AppException.Conflict("Personel eşitlemesi kapalı (Directory:Enabled).");

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var got = (await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").ToListAsync(ct))[0];
            if (!got) throw AppException.Conflict("Başka bir eşitleme şu anda çalışıyor. Bitince tekrar deneyin.");
            try { return await RunLockedAsync(preview, trigger, userName, ct); }
            finally { await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None); }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    async Task<DirectorySyncRun> RunLockedAsync(bool preview, string trigger, string userName, CancellationToken ct)
    {
        var run = new DirectorySyncRun { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, Trigger = trigger, UserName = userName, Status = preview ? "Önizleme" : "Çalışıyor" };
        var changes = new List<DirectorySyncChange>();

        IReadOnlyList<DirectoryPerson> people;
        try { people = await dir.SearchAllAsync(ct); }
        catch (DirectoryUnavailableException ex)
        {
            run.Status = "Başarısız"; run.Message = ex.Message + " Hiçbir personel kaydı değiştirilmedi."; run.FinishedAt = DateTimeOffset.UtcNow;
            db.DirectorySyncRuns.Add(run);
            await db.SaveChangesAsync(ct);
            log.LogWarning("AD eşitlemesi başarısız: {Message}", ex.Message);
            return run;
        }

        var prefix = o.Domain.Trim().ToLowerInvariant() + ":";
        var rules = await db.DirectoryRules.OrderBy(r => r.Priority).ThenBy(r => r.Value).ToListAsync(ct);
        // Yalnızca bu alan adının kayıtları: başka alan adından eşitlenen personel bu eşitlemede pasife alınmaz.
        var linked = await db.Employees.Where(e => e.DirectoryId != null && e.DirectoryId.StartsWith(prefix)).ToDictionaryAsync(e => e.DirectoryId!, ct);
        var manual = await db.Employees.Where(e => e.DirectoryId == null && e.RegistryNo != null).ToListAsync(ct);
        var companies = await db.Companies.ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var openItems = (await db.Items.Where(i => i.EmployeeId != null).GroupBy(i => i.EmployeeId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.N);
        var now = DateTimeOffset.UtcNow;
        var seen = new HashSet<string>();
        var deactivatedIds = new List<Guid>();
        run.Found = people.Count;
        var activeDirectory = linked.Values.Count(e => e.Source == EmployeeSource.Directory && e.IsActive);

        foreach (var p in people)
        {
            var dirId = prefix + p.Id;
            if (!seen.Add(dirId)) continue;
            var companyId = Resolve(rules, p);
            linked.TryGetValue(dirId, out var emp);

            if (emp is null)
            {
                if (companyId is null)
                {
                    run.Unmatched++;
                    changes.Add(new("Eşleşmeyen", p.DisplayName, $"{p.UserName} · AD şirket: {p.Company ?? "boş"} · {p.Department ?? "birim yok"}"));
                    continue;
                }
                if (p.Disabled) continue;
                emp = manual.FirstOrDefault(m => m.CompanyId == companyId && p.EmployeeNumber != null && m.RegistryNo == p.EmployeeNumber);
                if (emp is not null)
                {
                    manual.Remove(emp);
                    emp.DirectoryId = dirId; emp.Source = EmployeeSource.Directory;
                    changes.Add(new("Bağlandı", p.DisplayName, $"Elle girilmiş kayıt sicil no {p.EmployeeNumber} ile AD hesabına bağlandı"));
                    run.Updated++;
                }
                else
                {
                    emp = new Employee { Id = Guid.NewGuid(), CompanyId = companyId.Value, DirectoryId = dirId, Source = EmployeeSource.Directory, IsActive = true, FullName = p.DisplayName };
                    db.Employees.Add(emp);
                    changes.Add(new("Yeni", p.DisplayName, $"{companies[companyId.Value]} · {p.Department ?? "birim yok"}"));
                    run.Created++;
                }
                Apply(emp, p, now);
                linked[dirId] = emp;
                continue;
            }

            var before = (emp.FullName, emp.Email, emp.Title, emp.Department, emp.DirectoryUserName, emp.RegistryNo, emp.CompanyId, emp.IsActive);
            var oldName = emp.FullName;
            Apply(emp, p, now);
            if (companyId is Guid c && c != emp.CompanyId)
            {
                if (openItems.GetValueOrDefault(emp.Id) > 0)
                    changes.Add(new("Uyarı", p.DisplayName, $"Şirketi {companies[emp.CompanyId]} → {companies[c]} olmalı ama {openItems[emp.Id]} açık zimmeti var; önce iade alın"));
                else
                {
                    changes.Add(new("Şirket değişti", p.DisplayName, $"{companies[emp.CompanyId]} → {companies[c]}"));
                    emp.CompanyId = c;
                }
            }
            if (p.Disabled && emp.IsActive)
            {
                emp.IsActive = false;
                deactivatedIds.Add(emp.Id);
                run.Deactivated++;
                changes.Add(new("Pasife alındı", p.DisplayName, "AD'de devre dışı" + OpenNote(openItems, emp.Id)));
            }
            else if (!p.Disabled && !emp.IsActive)
            {
                emp.IsActive = true;
                changes.Add(new("Etkinleştirildi", p.DisplayName, "AD'de tekrar etkin"));
            }
            if (oldName != emp.FullName) changes.Add(new("Ad değişti", emp.FullName, $"Önceki: {oldName}"));
            if (before != (emp.FullName, emp.Email, emp.Title, emp.Department, emp.DirectoryUserName, emp.RegistryNo, emp.CompanyId, emp.IsActive)) run.Updated++;
        }

        // AD'de artık bulunmayanlar: yalnızca arama güvenilir görünüyorsa pasife alınır.
        var missing = linked.Values.Where(e => e.IsActive && e.Source == EmployeeSource.Directory && !seen.Contains(e.DirectoryId!)).ToList();
        var trustworthy = people.Count > 0 && people.Count >= activeDirectory * o.MinFoundRatio;
        if (missing.Count > 0 && !trustworthy)
        {
            run.Message = $"AD'den {people.Count} kayıt döndü; mevcut {activeDirectory} aktif AD personeline göre beklenenden az. " +
                          $"Arama kökü veya filtre yanlış olabilir; {missing.Count} kişi pasife alınmadı.";
            changes.Add(new("Uyarı", "Toplu pasife alma yapılmadı", run.Message));
        }
        else
        {
            foreach (var e in missing)
            {
                e.IsActive = false;
                deactivatedIds.Add(e.Id);
                run.Deactivated++;
                changes.Add(new("Pasife alındı", e.FullName, "AD'de bulunamadı" + OpenNote(openItems, e.Id)));
            }
        }

        // Pasife alınan personele bağlı AD uygulama hesapları kapatılır.
        var accounts = deactivatedIds.Count == 0 ? [] : await db.Users.Where(u => u.EmployeeId != null && deactivatedIds.Contains(u.EmployeeId.Value) && u.IsActive).ToListAsync(ct);
        foreach (var u in accounts)
        {
            u.IsActive = false;
            changes.Add(new("Hesap kapatıldı", u.DisplayName, $"Uygulama hesabı {u.UserName} kapatıldı, açık oturumları sonlandırıldı"));
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Changes = JsonSerializer.Serialize(changes.Take(1000), JsonSerializerOptions.Web);
        if (preview)
        {
            db.ChangeTracker.Clear();
            run.Message ??= "Önizleme: hiçbir kayıt değiştirilmedi.";
        }
        else
        {
            run.Status = "Başarılı";
            var offboarded = deactivatedIds.Where(id => openItems.GetValueOrDefault(id) > 0)
                .Select(id => (linked.Values.First(e => e.Id == id).FullName, openItems[id])).ToList();
            await notifier.DirectoryOffboardingAsync(run, offboarded);
            // Üzerinde cihaz veya hat kalan her pasif personel için ilişik kesme süreci açılır.
            foreach (var id in deactivatedIds)
            {
                var c = await offboarding.OpenAsync(linked.Values.First(e => e.Id == id), OffboardingService.SourceDirectory, userName, null, null, onlyIfHolding: true, ct);
                if (c is not null && c.OpenedAt >= now) changes.Add(new("İlişik kesme", linked.Values.First(e => e.Id == id).FullName, $"{c.No} iade süreci başlatıldı"));
            }
            run.Changes = JsonSerializer.Serialize(changes.Take(1000), JsonSerializerOptions.Web);
            db.AuditEvents.Add(new AuditEvent { At = now, UserName = userName, Action = "AD eşitlemesi", Entity = "Personel",
                Details = $"Bulunan {run.Found}, yeni {run.Created}, güncellenen {run.Updated}, pasife alınan {run.Deactivated}, eşleşmeyen {run.Unmatched}" });
        }
        db.DirectorySyncRuns.Add(run);
        await db.SaveChangesAsync(ct);
        if (!preview)
            foreach (var u in accounts) await DbTicketStore.RevokeUser(db, u.Id);
        log.LogInformation("AD eşitlemesi ({Status}): bulunan {Found}, yeni {Created}, güncellenen {Updated}, pasif {Deactivated}, eşleşmeyen {Unmatched}",
            run.Status, run.Found, run.Created, run.Updated, run.Deactivated, run.Unmatched);
        return run;
    }

    static string OpenNote(Dictionary<Guid, int> open, Guid id) => open.GetValueOrDefault(id) is > 0 and var n ? $" · {n} açık zimmeti var, iade sürecini başlatın" : "";

    static void Apply(Employee e, DirectoryPerson p, DateTimeOffset now)
    {
        e.FullName = p.DisplayName;
        e.Email = string.IsNullOrWhiteSpace(p.Email) ? e.Email : p.Email.Trim().ToLowerInvariant();
        e.Title = p.Title ?? e.Title;
        e.Department = p.Department ?? e.Department;
        if (!string.IsNullOrWhiteSpace(p.EmployeeNumber)) e.RegistryNo = p.EmployeeNumber;
        e.DirectoryUserName = p.UserName.ToLowerInvariant();
        e.DirectoryDn = p.Dn;
        e.DirectoryCompanyText = p.Company;
        e.DirectorySyncedAt = now;
    }

    /// <summary>İlk eşleşen kural (öncelik sırasıyla) şirketi belirler.</summary>
    public static Guid? Resolve(IEnumerable<DirectoryCompanyRule> rules, DirectoryPerson p)
    {
        foreach (var r in rules)
        {
            var v = p.Attribute(r.MatchType == DirectoryMatch.DnEndsWith ? "dn" : r.Attribute);
            if (string.IsNullOrEmpty(v)) continue;
            var ok = r.MatchType switch
            {
                DirectoryMatch.Equals => string.Equals(v.Trim(), r.Value.Trim(), StringComparison.OrdinalIgnoreCase),
                DirectoryMatch.Contains => v.Contains(r.Value.Trim(), StringComparison.OrdinalIgnoreCase),
                DirectoryMatch.DnEndsWith => v.Replace(" ", "").EndsWith(r.Value.Replace(" ", ""), StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (ok) return r.CompanyId;
        }
        return null;
    }
}

using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Services;

public class OffboardingOptions
{
    /// <summary>Her yeni ilişik kesme sürecine eklenen görevler (ör. "Giriş kartı teslim alındı").</summary>
    public List<string> DefaultTasks { get; set; } = [];
}

/// <summary>
/// İlişik kesme süreci. AD'de pasife düşen veya elle pasife alınan personelin üzerinde cihaz ya da hat varsa
/// süreç otomatik açılır. Tüm cihazlar iade alınmadan, doğrudan verilen hatlar geri alınmadan ve görevler
/// tamamlanmadan kapatılamaz; kapatıldığında dondurulmuş bir ilişik kesme belgesi üretilir.
/// </summary>
public class OffboardingService(AppDbContext db, OffboardingOptions o, Notifier notifier)
{
    public const string SourceDirectory = "AD eşitlemesi";
    public const string SourceDeactivated = "Personel pasife alındı";
    public const string SourceManual = "Elle";

    /// <summary>Personelin üzerindeki (tüm kapsamlar dahil) cihaz ve doğrudan verilen hat sayısı.</summary>
    public static async Task<(int Items, int Lines)> HoldingsAsync(AppDbContext db, Guid employeeId, CancellationToken ct = default) =>
        (await db.Items.CountAsync(i => i.EmployeeId == employeeId, ct),
         await db.PhoneLines.CountAsync(l => l.EmployeeId == employeeId && l.Status != LineStatus.Cancelled, ct));

    /// <summary>
    /// Açık süreç varsa onu döndürür, yoksa açar. <paramref name="onlyIfHolding"/> doğruysa yalnızca personelin üzerinde
    /// cihaz veya hat varken açar. Değişiklikler çağıranın SaveChanges'i ile yazılır.
    /// </summary>
    public async Task<OffboardingCase?> OpenAsync(Employee emp, string source, string user, DateOnly? lastDay, string? note, bool onlyIfHolding, CancellationToken ct = default)
    {
        var existing = await db.OffboardingCases.FirstOrDefaultAsync(c => c.EmployeeId == emp.Id && c.Status == OffboardingStatus.Open, ct)
                       ?? db.OffboardingCases.Local.FirstOrDefault(c => c.EmployeeId == emp.Id && c.Status == OffboardingStatus.Open);
        if (existing is not null) return existing;
        var (items, lines) = await HoldingsAsync(db, emp.Id, ct);
        if (onlyIfHolding && items + lines == 0) return null;
        var now = DateTimeOffset.UtcNow;
        var c = new OffboardingCase
        {
            Id = Guid.NewGuid(), No = await Counters.DocumentNoAsync(db, "ILS", now, ct), Status = OffboardingStatus.Open,
            CompanyId = emp.CompanyId, EmployeeId = emp.Id, Source = source, LastWorkingDay = lastDay,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), OpenedAt = now, OpenedBy = user
        };
        foreach (var t in o.DefaultTasks.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct())
            c.Tasks.Add(new OffboardingTask { Id = Guid.NewGuid(), CaseId = c.Id, Title = t.Trim() });
        db.OffboardingCases.Add(c);
        db.AuditEvents.Add(new AuditEvent { At = now, UserName = user, Action = "İlişik kesme başlatıldı", Entity = "İlişik kesme", EntityId = c.Id.ToString(),
            Details = $"{c.No} · {emp.FullName} · {source} · {items} cihaz, {lines} hat" });
        await notifier.OffboardingOpenedAsync(c, emp, items, lines);
        return c;
    }

    /// <summary>Kapatmayı engelleyen durumlar. Kullanıcının kapsamı dışındaki cihazlar da sayılır.</summary>
    public static async Task<List<string>> BlockersAsync(AppDbContext db, OffboardingCase c, CancellationToken ct = default)
    {
        var (items, lines) = await HoldingsAsync(db, c.EmployeeId, ct);
        var tasks = await db.OffboardingTasks.CountAsync(t => t.CaseId == c.Id && !t.Done, ct);
        var b = new List<string>();
        if (items > 0) b.Add($"{items} cihaz henüz iade alınmadı.");
        if (lines > 0) b.Add($"{lines} hat henüz geri alınmadı.");
        if (tasks > 0) b.Add($"{tasks} görev tamamlanmadı.");
        return b;
    }

    /// <summary>Süreç açıldığından bu yana iade alınan cihazlar ve geri alınan hatlar (belge ve ekran için).</summary>
    public static async Task<List<OffboardingReturned>> ReturnedAsync(AppDbContext db, OffboardingCase c, DateTimeOffset? until = null, CancellationToken ct = default)
    {
        var end = until ?? DateTimeOffset.MaxValue;
        var items = await db.Assignments
            .Where(x => x.EmployeeId == c.EmployeeId && x.Kind == AssignmentKind.Return && x.At >= c.OpenedAt && x.At <= end)
            .SelectMany(x => x.Lines.Select(l => new { x.No, x.At, l.Item!.Tag, Brand = l.Item.AssetModel!.Manufacturer!.Name, Model = l.Item.AssetModel.Name, l.Item.Serial, l.Condition }))
            .ToListAsync(ct);
        var lines = await db.PhoneLineEvents.Where(e => e.EmployeeId == c.EmployeeId && e.Type == "Geri alındı" && e.At >= c.OpenedAt && e.At <= end)
            .Join(db.PhoneLines, e => e.LineId, l => l.Id, (e, l) => new { e.At, l.Number, l.Operator, l.Iccid })
            .ToListAsync(ct);
        return items.Select(i => new OffboardingReturned("Cihaz", i.No,
                    $"{i.Tag} · {i.Brand} {i.Model}{(i.Serial is null ? "" : $" · SN {i.Serial}")}{(i.Condition is null ? "" : $" · {i.Condition}")}", i.At))
            .Concat(lines.Select(l => new OffboardingReturned("Hat", LineLabels.Number(l.Number), $"{l.Operator}{(l.Iccid is null ? "" : $" · SIM {l.Iccid}")}", l.At)))
            .OrderBy(r => r.At).ToList();
    }
}

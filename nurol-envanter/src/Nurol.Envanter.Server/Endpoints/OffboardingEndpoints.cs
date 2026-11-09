using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Server.Services;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

/// <summary>
/// İlişik kesme (işten ayrılan personel için iade kontrol listesi). Görme: şirkette okuma; yönetme ve kapatma: şirkette yazma yetkisi.
/// </summary>
public static class OffboardingEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/offboarding", async (HttpContext ctx, AppDbContext db, OffboardingStatus? status, string? q, Guid? employeeId) =>
        {
            var a = await ctx.Access();
            var cases = Scoped(db, a).AsNoTracking();
            if (status is OffboardingStatus s) cases = cases.Where(c => c.Status == s);
            if (employeeId is Guid e) cases = cases.Where(c => c.EmployeeId == e);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var t = $"%{q.Trim()}%";
                cases = cases.Where(c => EF.Functions.ILike(c.No, t) || EF.Functions.ILike(c.Employee!.FullName, t) || EF.Functions.ILike(c.Employee.RegistryNo ?? "", t));
            }
            return await Rows(db, cases.OrderBy(c => c.Status).ThenByDescending(c => c.OpenedAt).Take(500));
        });

        api.MapGet("/offboarding/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) => await Detail(db, await ctx.Access(), id));

        api.MapPost("/offboarding", async (OffboardingStart req, HttpContext ctx, AppDbContext db, OffboardingService svc) =>
        {
            var a = await ctx.Access();
            var emp = await a.Employees(db).FirstOrDefaultAsync(e => e.Id == req.EmployeeId) ?? throw AppException.NotFound("Personel");
            if (!a.CanWriteCompany(emp.CompanyId)) throw AppException.Forbidden();
            if (await db.OffboardingCases.AnyAsync(c => c.EmployeeId == emp.Id && c.Status == OffboardingStatus.Open))
                throw AppException.Conflict($"{emp.FullName} için açık bir ilişik kesme süreci zaten var.");
            var c = (await svc.OpenAsync(emp, OffboardingService.SourceManual, a.UserName, req.LastWorkingDay, req.Note, onlyIfHolding: false))!;
            await db.SaveChangesAsync();
            return Results.Ok(new { c.Id });
        });

        api.MapPut("/offboarding/{id:guid}", async (Guid id, OffboardingStart req, HttpContext ctx, AppDbContext db) =>
        {
            var (a, c) = await Writable(ctx, db, id);
            c.LastWorkingDay = req.LastWorkingDay;
            c.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, a, id));
        });

        api.MapPost("/offboarding/{id:guid}/tasks", async (Guid id, OffboardingTaskSave req, HttpContext ctx, AppDbContext db) =>
        {
            var (a, c) = await Writable(ctx, db, id);
            var title = (req.Title ?? "").Trim();
            if (title.Length is 0 or > 200) throw AppException.Invalid("Görev 1-200 karakter olmalıdır.");
            db.OffboardingTasks.Add(new OffboardingTask { Id = Guid.NewGuid(), CaseId = c.Id, Title = title });
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, a, id));
        });

        api.MapPut("/offboarding/{id:guid}/tasks/{taskId:guid}", async (Guid id, Guid taskId, OffboardingTaskToggle req, HttpContext ctx, AppDbContext db) =>
        {
            var (a, c) = await Writable(ctx, db, id);
            var t = await db.OffboardingTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.CaseId == c.Id) ?? throw AppException.NotFound("Görev");
            t.Done = req.Done;
            t.DoneAt = req.Done ? DateTimeOffset.UtcNow : null;
            t.DoneBy = req.Done ? (string.IsNullOrEmpty(a.DisplayName) ? a.UserName : a.DisplayName) : null;
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, a, id));
        });

        api.MapDelete("/offboarding/{id:guid}/tasks/{taskId:guid}", async (Guid id, Guid taskId, HttpContext ctx, AppDbContext db) =>
        {
            var (a, c) = await Writable(ctx, db, id);
            var t = await db.OffboardingTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.CaseId == c.Id) ?? throw AppException.NotFound("Görev");
            db.OffboardingTasks.Remove(t);
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, a, id));
        });

        api.MapPost("/offboarding/{id:guid}/complete", async (Guid id, HttpContext ctx, AppDbContext db, IFileStore files) =>
        {
            var a = await ctx.Access();
            await using var tx = await db.Database.BeginTransactionAsync();
            // Aynı süreci iki kişinin aynı anda kapatmasını önler.
            var c = await db.OffboardingCases.FromSql($"SELECT * FROM offboarding_cases WHERE id = {id} FOR UPDATE").Include(x => x.Tasks).FirstOrDefaultAsync()
                    ?? throw AppException.NotFound("İlişik kesme süreci");
            if (!a.CanReadCompany(c.CompanyId)) throw AppException.NotFound("İlişik kesme süreci");
            if (!a.CanWriteCompany(c.CompanyId)) throw AppException.Forbidden();
            if (c.Status != OffboardingStatus.Open) throw AppException.Conflict("Süreç açık değil.");
            var blockers = await OffboardingService.BlockersAsync(db, c);
            if (blockers.Count > 0) throw AppException.Conflict("İlişik kesilemez: " + string.Join(" ", blockers));

            var now = DateTimeOffset.UtcNow;
            var emp = await db.Employees.Include(e => e.Company).FirstAsync(e => e.Id == c.EmployeeId);
            var who = string.IsNullOrEmpty(a.DisplayName) ? a.UserName : a.DisplayName;
            var returned = await OffboardingService.ReturnedAsync(db, c, now);
            var snap = new OffboardingSnapshot(c.No, emp.Company!.Name, emp.FullName, emp.RegistryNo, emp.Title, emp.Department,
                c.LastWorkingDay?.ToString("dd.MM.yyyy"), TimeZones.Format(c.OpenedAt), TimeZones.Format(now), who, c.Source,
                returned.Select(r => (r.Kind, r.Reference, r.Text, TimeZones.Format(r.At)[..10])).ToList(),
                c.Tasks.OrderBy(t => t.DoneAt).Select(t => (t.Title, t.DoneBy, t.DoneAt is DateTimeOffset d ? TimeZones.Format(d)[..10] : null)).ToList(), c.Note);
            var pdf = OffboardingPdf.Render(snap);
            c.CertificateKey = $"ilisik/{c.Id}/belge-{Guid.NewGuid():N}.pdf";
            c.CertificateSha256 = Convert.ToHexStringLower(SHA256.HashData(pdf));
            await files.PutAsync(c.CertificateKey, pdf, "application/pdf");
            c.Status = OffboardingStatus.Completed;
            c.CompletedAt = now;
            c.CompletedBy = who;
            Audit.Add(db, a, "İlişik kesildi", "İlişik kesme", c.Id, $"{c.No} · {emp.FullName} · {returned.Count} kalem iade");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(await Detail(db, a, id));
        });

        api.MapPost("/offboarding/{id:guid}/cancel", async (Guid id, OffboardingCancel req, HttpContext ctx, AppDbContext db) =>
        {
            var (a, c) = await Writable(ctx, db, id);
            var reason = (req.Reason ?? "").Trim();
            if (reason.Length < 3) throw AppException.Invalid("İptal nedenini yazın.");
            c.Status = OffboardingStatus.Cancelled;
            c.CancelReason = reason;
            c.CompletedAt = DateTimeOffset.UtcNow;
            c.CompletedBy = string.IsNullOrEmpty(a.DisplayName) ? a.UserName : a.DisplayName;
            Audit.Add(db, a, "İlişik kesme iptal", "İlişik kesme", c.Id, $"{c.No} · {reason}");
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, a, id));
        });

        api.MapGet("/offboarding/{id:guid}/certificate", async (Guid id, HttpContext ctx, AppDbContext db, IFileStore files) =>
        {
            var a = await ctx.Access();
            var c = await Scoped(db, a).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("İlişik kesme süreci");
            if (c.CertificateKey is null) throw AppException.NotFound("İlişik kesme belgesi");
            var bytes = await files.GetAsync(c.CertificateKey);
            if (c.CertificateSha256 is not null && Convert.ToHexStringLower(SHA256.HashData(bytes)) != c.CertificateSha256)
                throw AppException.Conflict("Belge bütünlük kontrolünden geçemedi. Sistem yöneticisine bildirin.");
            return Results.File(bytes, "application/pdf", $"{c.No}.pdf");
        });
    }

    static IQueryable<OffboardingCase> Scoped(AppDbContext db, Access a) =>
        a.IsSystemAdmin ? db.OffboardingCases : db.OffboardingCases.Where(c => a.Companies.Contains(c.CompanyId));

    static async Task<(Access, OffboardingCase)> Writable(HttpContext ctx, AppDbContext db, Guid id)
    {
        var a = await ctx.Access();
        var c = await Scoped(db, a).FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("İlişik kesme süreci");
        if (!a.CanWriteCompany(c.CompanyId)) throw AppException.Forbidden();
        if (c.Status != OffboardingStatus.Open) throw AppException.Conflict("Süreç kapanmış; değişiklik yapılamaz.");
        return (a, c);
    }

    internal static Task<List<OffboardingRow>> Rows(AppDbContext db, IQueryable<OffboardingCase> q) =>
        q.Select(c => new OffboardingRow(c.Id, c.No, c.Status, c.EmployeeId, c.Employee!.FullName, c.Employee.Department, c.Employee.Company!.Name,
            c.Source, c.OpenedAt, c.LastWorkingDay,
            db.Items.Count(i => i.EmployeeId == c.EmployeeId),
            db.PhoneLines.Count(l => l.EmployeeId == c.EmployeeId && l.Status != LineStatus.Cancelled),
            c.Tasks.Count(t => !t.Done), c.CompletedAt)).ToListAsync();

    static async Task<OffboardingDetail> Detail(AppDbContext db, Access a, Guid id)
    {
        var c = await Scoped(db, a).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("İlişik kesme süreci");
        var row = (await Rows(db, db.OffboardingCases.Where(x => x.Id == id))).First();
        var (items, _) = await ItemEndpoints.ListAsync(db, a, new ItemQuery(null, null, null, null, null, null, null, "tag", false, 1, 1000, c.EmployeeId));
        // Doğrudan verilen hatlar ve personelin cihazlarına takılı hatlar birlikte gösterilir.
        var lines = await LineEndpoints.Rows(a, LineEndpoints.Scoped(db, a).AsNoTracking()
            .Where(l => l.Status != LineStatus.Cancelled && (l.EmployeeId == c.EmployeeId || (l.Item != null && l.Item.EmployeeId == c.EmployeeId))).OrderBy(l => l.Number));
        var tasks = await db.OffboardingTasks.Where(t => t.CaseId == id).OrderBy(t => t.Title)
            .Select(t => new OffboardingTaskDto(t.Id, t.Title, t.Done, t.DoneAt, t.DoneBy)).ToListAsync();
        var open = c.Status == OffboardingStatus.Open;
        var blockers = open ? await OffboardingService.BlockersAsync(db, c) : [];
        if (open && row.OpenItems > items.Count) blockers.Add($"{row.OpenItems - items.Count} cihaz yetki kapsamınız dışındaki depolarda; o depoların sorumluları iade almalı.");
        var returned = await OffboardingService.ReturnedAsync(db, c, c.Status == OffboardingStatus.Open ? null : c.CompletedAt);
        var manage = open && a.CanWriteCompany(c.CompanyId);
        return new OffboardingDetail(row, c.Note, c.OpenedBy, c.CompletedBy, c.CancelReason, items, lines, tasks, returned,
            manage, manage && blockers.Count == 0, blockers, c.CertificateKey is not null);
    }
}

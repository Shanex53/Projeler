using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Server.Services;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

/// <summary>AD bağlantı durumu, şirket eşleme kuralları ve eşitleme. Yalnızca sistem yöneticisi.</summary>
public static class DirectoryEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/directory/status", async (HttpContext ctx, AppDbContext db, DirectoryOptions o) =>
        {
            (await ctx.Access()).RequireSystemAdmin();
            var runs = await db.DirectorySyncRuns.OrderByDescending(r => r.StartedAt).Take(10).ToListAsync();
            return new DirectoryStatusDto(o.Enabled, o.LoginEnabled, o.Configured, o.Server, o.Port, o.UseSsl, o.BaseDn, o.Filter, o.Domain,
                o.IdAttribute, o.UserNameAttribute, o.ScheduleNote, runs.Select(r => ToDto(r, false)).ToList());
        });

        api.MapPost("/directory/test", async (HttpContext ctx, IDirectoryClient dir, DirectoryOptions o) =>
        {
            (await ctx.Access()).RequireSystemAdmin();
            if (!o.Configured) return new DirectoryTestResult(false, "Bağlantı yapılandırılmamış: Directory:Server ve Directory:BaseDn değerlerini Helm ile verin.", null);
            try
            {
                var people = await dir.SearchAllAsync(ctx.RequestAborted);
                return new DirectoryTestResult(true, $"Bağlantı başarılı. Arama kökünde filtreye uyan {people.Count} hesap bulundu.", people.Count);
            }
            catch (DirectoryUnavailableException ex) { return new DirectoryTestResult(false, ex.Message, null); }
        });

        api.MapPost("/directory/sync", async (bool? preview, HttpContext ctx, DirectorySync sync) =>
        {
            var a = await ctx.Access(); a.RequireSystemAdmin();
            var run = await sync.RunAsync(preview ?? true, "Elle", a.UserName, ctx.RequestAborted);
            return ToDto(run, true);
        });

        api.MapGet("/directory/runs/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            (await ctx.Access()).RequireSystemAdmin();
            var r = await db.DirectorySyncRuns.FindAsync(id) ?? throw AppException.NotFound("Eşitleme kaydı");
            return ToDto(r, true);
        });

        api.MapGet("/directory/rules", async (HttpContext ctx, AppDbContext db) =>
        {
            (await ctx.Access()).RequireSystemAdmin();
            return await db.DirectoryRules.OrderBy(r => r.Priority).ThenBy(r => r.Value)
                .Select(r => new DirectoryRuleDto(r.Id, r.Priority, r.Attribute, r.MatchType, r.Value, r.CompanyId, r.Company!.Name)).ToListAsync();
        });
        api.MapPost("/directory/rules", async (DirectoryRuleSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireSystemAdmin();
            Validate(req);
            if (!await db.Companies.AnyAsync(c => c.Id == req.CompanyId)) throw AppException.Invalid("Şirket seçin.");
            var r = new DirectoryCompanyRule { Id = Guid.NewGuid() };
            Apply(r, req);
            db.DirectoryRules.Add(r);
            Audit.Add(db, a, "Oluşturma", "AD şirket kuralı", r.Id, $"{r.Attribute} {r.MatchType} {r.Value}");
            await db.SaveChangesAsync();
            return Results.Ok(new { r.Id });
        });
        api.MapPut("/directory/rules/{id:guid}", async (Guid id, DirectoryRuleSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireSystemAdmin();
            Validate(req);
            var r = await db.DirectoryRules.FindAsync(id) ?? throw AppException.NotFound("Kural");
            Apply(r, req);
            Audit.Add(db, a, "Güncelleme", "AD şirket kuralı", r.Id, $"{r.Attribute} {r.MatchType} {r.Value}");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
        api.MapDelete("/directory/rules/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireSystemAdmin();
            var n = await db.DirectoryRules.Where(r => r.Id == id).ExecuteDeleteAsync();
            Audit.Add(db, a, "Silme", "AD şirket kuralı", id, null);
            await db.SaveChangesAsync();
            return n == 0 ? throw AppException.NotFound("Kural") : Results.NoContent();
        });
    }

    static void Validate(DirectoryRuleSave r)
    {
        if (!DirectoryMatch.All.Contains(r.MatchType)) throw AppException.Invalid("Geçerli bir eşleme türü seçin.");
        if (string.IsNullOrWhiteSpace(r.Value)) throw AppException.Invalid("Eşleşecek değeri yazın.");
        if (r.MatchType != DirectoryMatch.DnEndsWith && r.Attribute is not ("company" or "department" or "title" or "mail" or "employeeid"))
            throw AppException.Invalid("Öznitelik company, department, title, mail veya employeeid olmalıdır.");
    }

    static void Apply(DirectoryCompanyRule r, DirectoryRuleSave s)
    {
        r.Priority = s.Priority; r.MatchType = s.MatchType; r.Value = s.Value.Trim(); r.CompanyId = s.CompanyId;
        r.Attribute = s.MatchType == DirectoryMatch.DnEndsWith ? "dn" : s.Attribute;
    }

    internal static DirectorySyncRunDto ToDto(DirectorySyncRun r, bool withChanges) => new(
        r.Id, r.StartedAt, r.FinishedAt, r.Status, r.Trigger, r.UserName, r.Found, r.Created, r.Updated, r.Deactivated, r.Unmatched, r.Message,
        withChanges ? JsonSerializer.Deserialize<List<DirectorySyncChange>>(r.Changes, JsonSerializerOptions.Web) ?? [] : []);
}

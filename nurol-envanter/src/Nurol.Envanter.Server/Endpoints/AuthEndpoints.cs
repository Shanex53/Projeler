using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

public static class AuthEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/auth/login", Login).AllowAnonymous().RequireRateLimiting("login");
        api.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();
        api.MapGet("/auth/me", Me);
        api.MapGet("/auth/options", (DirectoryOptions o) => new { directoryLogin = o.LoginEnabled }).AllowAnonymous();
    }

    static async Task<IResult> Login(LoginRequest req, HttpContext ctx, UserManager<AppUser> users, AppDbContext db,
        IDirectoryClient dir, DirectoryOptions dirOptions, ILogger<Program> log)
    {
        const string generic = "Kullanıcı adı veya parola hatalı.";
        if (string.IsNullOrWhiteSpace(req.UserName) || string.IsNullOrEmpty(req.Password))
            throw AppException.Invalid("Kullanıcı adı ve parola gereklidir.");

        var wantsDirectory = req.AccountType == "Directory";
        var name = wantsDirectory ? LdapDirectoryClient.NormalizeLogin(req.UserName) : req.UserName.Trim().ToLowerInvariant();
        var user = await users.FindByNameAsync(name);
        // Seçilen hesap türü hesabın kendisiyle uyuşmalı; AD hesabı yerel parolayla, yerel hesap AD ile doğrulanmaz.
        if (user is null || (req.AccountType is not null && user.AccountType != req.AccountType)) throw new AppException(401, generic);
        if (!user.IsActive) throw new AppException(401, "Hesabınız devre dışı. Yöneticinize başvurun.");
        if (await users.IsLockedOutAsync(user))
            throw new AppException(429, "Çok fazla hatalı deneme yapıldı. Hesap 15 dakika kilitlendi.");

        bool ok;
        if (user.AccountType == "Directory")
        {
            if (!dirOptions.LoginEnabled) throw new AppException(401, "Kurum hesabıyla giriş şu an kapalı. Yöneticinize başvurun.");
            DirectoryPerson? person;
            try
            {
                person = await dir.FindUserAsync(name, ctx.RequestAborted);
                if (person is not null && person.Disabled)
                {
                    log.LogWarning("AD'de devre dışı hesapla giriş denemesi: {User}", name);
                    throw new AppException(401, "AD hesabınız devre dışı. Bilgi işlem birimine başvurun.");
                }
                // Hesap, yetki verildiği AD kimliğiyle aynı olmalı (aynı ada sahip yeni bir AD hesabı yetkiyi devralamaz).
                ok = person is not null && user.DirectoryId == dirOptions.Domain.Trim().ToLowerInvariant() + ":" + person.Id
                     && await dir.VerifyPasswordAsync(person.Dn, req.Password, ctx.RequestAborted);
            }
            catch (DirectoryUnavailableException ex)
            {
                log.LogError(ex, "AD girişi sırasında dizine ulaşılamadı");
                throw new AppException(503, "Kurum dizinine şu an ulaşılamıyor. Bir süre sonra tekrar deneyin.");
            }
        }
        else ok = await users.CheckPasswordAsync(user, req.Password);

        if (!ok)
        {
            await users.AccessFailedAsync(user);
            log.LogWarning("Hatalı giriş denemesi: {User}", user.UserName);
            throw new AppException(401, generic);
        }
        await users.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await users.UpdateAsync(user);

        var roles = await users.GetRolesAsync(user);
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.UserName!));
        foreach (var r in roles) identity.AddClaim(new Claim(ClaimTypes.Role, r));
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });
        db.AuditEvents.Add(new AuditEvent { At = DateTimeOffset.UtcNow, UserName = user.UserName!, Action = user.AccountType == "Directory" ? "Giriş (AD)" : "Giriş", Entity = "Kullanıcı", EntityId = user.Id });
        await db.SaveChangesAsync();
        return Results.Ok(await BuildMe(db, user.Id));
    }

    static async Task<IResult> Me(HttpContext ctx, AppDbContext db)
    {
        await ctx.Access();
        return Results.Ok(await BuildMe(db, ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!));
    }

    internal static async Task<MeDto> BuildMe(AppDbContext db, string userId)
    {
        var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == userId);
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == userId select r.Name!).ToListAsync();
        var scopes = await db.UserScopes.Where(s => s.UserId == userId)
            .Select(s => new ScopeDto(s.CompanyId, s.Company!.Name, s.WarehouseId, s.Warehouse != null ? s.Warehouse.Name : null)).ToListAsync();
        var canWrite = roles.Any(r => r is Roles.SystemAdmin or Roles.CompanyAdmin or Roles.WarehouseManager);
        return new MeDto(u.UserName!, u.DisplayName, u.AccountType, roles, scopes, canWrite, roles.Contains(Roles.SystemAdmin));
    }
}

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
    }

    static async Task<IResult> Login(LoginRequest req, HttpContext ctx, UserManager<AppUser> users, AppDbContext db, ILogger<Program> log)
    {
        const string generic = "Kullanıcı adı veya parola hatalı.";
        if (string.IsNullOrWhiteSpace(req.UserName) || string.IsNullOrEmpty(req.Password))
            throw AppException.Invalid("Kullanıcı adı ve parola gereklidir.");

        var user = await users.FindByNameAsync(req.UserName.Trim());
        if (user is null || user.AccountType != "Local")
        {
            // AD hesapları Aşama 2'de AD'de doğrulanacak; yerel parolaya düşülmez.
            throw new AppException(401, generic);
        }
        if (!user.IsActive) throw new AppException(401, "Hesabınız devre dışı. Yöneticinize başvurun.");
        if (await users.IsLockedOutAsync(user))
            throw new AppException(429, "Çok fazla hatalı deneme yapıldı. Hesap 15 dakika kilitlendi.");

        if (!await users.CheckPasswordAsync(user, req.Password))
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
        db.AuditEvents.Add(new AuditEvent { At = DateTimeOffset.UtcNow, UserName = user.UserName!, Action = "Giriş", Entity = "Kullanıcı", EntityId = user.Id });
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

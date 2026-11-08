using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Infrastructure;

public static class Startup
{
    /// <summary>Migration'ları PostgreSQL advisory lock ile uygular; birden çok pod aynı anda başlatsa da tek kez çalışır.</summary>
    public static async Task MigrateAsync(IServiceProvider sp, bool seedRoles)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();
        await db.Database.OpenConnectionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(472019)");
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count > 0)
            {
                log.LogInformation("Uygulanacak migration: {List}", string.Join(", ", pending));
                await db.Database.MigrateAsync();
            }
            if (seedRoles)
            {
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                foreach (var r in Roles.All)
                    if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole(r));
            }
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(472019)");
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Hiç sistem yöneticisi yoksa Secret'tan verilen ilk yönetici hesabını oluşturur.
    /// Üretimde varsayılan parola yoktur; değer verilmezse hesap oluşturulmaz.
    /// </summary>
    public static async Task EnsureBootstrapAdminAsync(IServiceProvider sp, IConfiguration cfg)
    {
        var userName = cfg["Bootstrap:AdminUserName"];
        var password = cfg["Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)) return;
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();
        var adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == Roles.SystemAdmin);
        if (adminRole is null) return; // Şema henüz kurulmamış.
        if (await db.UserRoles.AnyAsync(ur => ur.RoleId == adminRole.Id)) return;

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = userName.Trim().ToLowerInvariant(), DisplayName = "Sistem Yöneticisi", AccountType = "Local", LockoutEnabled = true };
        var res = await users.CreateAsync(user, password);
        if (!res.Succeeded)
        {
            log.LogError("İlk yönetici oluşturulamadı: {Errors}", string.Join(", ", res.Errors.Select(e => e.Code)));
            return;
        }
        await users.AddToRoleAsync(user, Roles.SystemAdmin);
        log.LogInformation("İlk sistem yöneticisi oluşturuldu: {User}", user.UserName);
    }
}

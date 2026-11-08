using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;

namespace Nurol.Envanter.Server.Infrastructure;

/// <summary>
/// Oturumları PostgreSQL'de tutar. Cookie yalnızca oturum anahtarını taşır; sunucudan silinen oturum
/// tüm pod'larda anında geçersiz olur. Pod değişimi oturumu düşürmez.
/// </summary>
public class DbTicketStore(IServiceScopeFactory scopes) : ITicketStore
{
    static readonly TicketSerializer Serializer = TicketSerializer.Default;

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Sessions.Add(new ServerSession
        {
            Id = id,
            UserId = ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
            Ticket = Serializer.Serialize(ticket),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddHours(8)
        });
        await db.SaveChangesAsync();
        return id;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Sessions.Where(s => s.Id == key).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.Ticket, Serializer.Serialize(ticket))
            .SetProperty(s => s.ExpiresAt, ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddHours(8)));
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == key);
        if (s is null || s.ExpiresAt < DateTimeOffset.UtcNow) return null;
        return Serializer.Deserialize(s.Ticket);
    }

    public async Task RemoveAsync(string key)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Sessions.Where(s => s.Id == key).ExecuteDeleteAsync();
    }

    public static Task RevokeUser(AppDbContext db, string userId) =>
        db.Sessions.Where(s => s.UserId == userId).ExecuteDeleteAsync();
}

/// <summary>Süresi dolmuş oturumları periyodik olarak temizler.</summary>
public class SessionCleanup(IServiceScopeFactory scopes, ILogger<SessionCleanup> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Sessions.Where(s => s.ExpiresAt < DateTimeOffset.UtcNow).ExecuteDeleteAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Oturum temizliği başarısız");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

/// <summary>
/// CSRF koruması: cookie SameSite=Strict ve durum değiştiren /api isteklerinde özel başlık zorunlu.
/// Tarayıcılar başka siteden bu başlığı CORS izni olmadan gönderemez; uygulama CORS açmaz.
/// </summary>
public class CsrfHeaderMiddleware(RequestDelegate next)
{
    public const string Header = "X-NE-Request";

    public Task Invoke(HttpContext ctx)
    {
        if (ctx.Request.Path.StartsWithSegments("/api") &&
            !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method) &&
            !ctx.Request.Headers.ContainsKey(Header))
        {
            ctx.Response.StatusCode = 400;
            return ctx.Response.WriteAsJsonAsync(new Shared.ApiError("İstek güvenlik kontrolünden geçemedi. Sayfayı yenileyin."));
        }
        return next(ctx);
    }
}

public static class CookieSetup
{
    public static void Configure(CookieAuthenticationOptions o, bool secure)
    {
        o.Cookie.Name = "ne_oturum";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    }
}

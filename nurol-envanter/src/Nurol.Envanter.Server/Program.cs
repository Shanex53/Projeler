using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Endpoints;
using Nurol.Envanter.Server.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// Kestrel: 20 MB evrak + form başlıkları için pay.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = UploadRules.MaxBytes + 1024 * 1024);

var cs = cfg.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default tanımlı değil.");
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());

builder.Services.AddIdentityCore<AppUser>(o =>
    {
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireDigit = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
        o.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyz0123456789.-_@";
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// Cookie şifreleme anahtarları PostgreSQL'de ortak: çoklu pod'da oturum düşmez.
builder.Services.AddDataProtection().SetApplicationName("nurol-envanter").PersistKeysToDbContext<AppDbContext>();

builder.Services.AddSingleton<DbTicketStore>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<DbTicketStore>((o, store) =>
    {
        CookieSetup.Configure(o, cfg.GetValue("Security:SecureCookies", !builder.Environment.IsDevelopment()));
        o.SessionStore = store;
    });
builder.Services.AddAuthorization();
builder.Services.AddHostedService<SessionCleanup>();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = cfg.GetValue("Security:LoginAttemptsPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
    o.OnRejected = (c, _) => new ValueTask(c.HttpContext.Response.WriteAsJsonAsync(new Nurol.Envanter.Shared.ApiError("Çok fazla giriş denemesi. Bir dakika sonra tekrar deneyin.")));
});

var storage = cfg.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
builder.Services.AddSingleton(storage);
builder.Services.AddSingleton<IFileStore>(storage.Provider.Equals("S3", StringComparison.OrdinalIgnoreCase) ? new S3Store(storage) : new FileSystemStore(storage));

var directory = cfg.GetSection("Directory").Get<DirectoryOptions>() ?? new DirectoryOptions();
builder.Services.AddSingleton(directory);
builder.Services.AddSingleton<IDirectoryClient, LdapDirectoryClient>();
builder.Services.AddScoped<Nurol.Envanter.Server.Services.DirectorySync>();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

// "migrate" argümanı: Kubernetes migration Job'u şemayı günceller ve çıkar.
if (storage.CreateBucket && app.Services.GetRequiredService<IFileStore>() is S3Store s3)
    await s3.EnsureBucketAsync(app.Logger);
if (args.Contains("migrate"))
{
    await Startup.MigrateAsync(app.Services, seedRoles: true);
    await Startup.EnsureBootstrapAdminAsync(app.Services, cfg);
    return;
}
// "ad-sync" argümanı: Kubernetes CronJob'u AD personel eşitlemesini çalıştırır ve çıkar.
if (args.Contains("ad-sync"))
{
    await using var syncScope = app.Services.CreateAsyncScope();
    var run = await syncScope.ServiceProvider.GetRequiredService<Nurol.Envanter.Server.Services.DirectorySync>().RunAsync(false, "Zamanlanmış", "ad-eşitleme");
    Environment.ExitCode = run.Status == "Başarılı" ? 0 : 1;
    return;
}
if (cfg.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment()))
    await Startup.MigrateAsync(app.Services, seedRoles: true);
await Startup.EnsureBootstrapAdminAsync(app.Services, cfg);
if (app.Environment.IsDevelopment() && cfg.GetValue("Seed:SampleData", false))
{
    await using var seedScope = app.Services.CreateAsyncScope();
    if (await Startup.SchemaReadyAsync(seedScope.ServiceProvider.GetRequiredService<AppDbContext>()))
        await DevSeeder.RunAsync(app.Services);
}

app.UseForwardedHeaders();
app.UseMiddleware<ErrorMiddleware>();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";
    if (!ctx.Request.Path.StartsWithSegments("/api"))
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; frame-ancestors 'none'";
    await next();
});
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CsrfHeaderMiddleware>();

app.MapGet("/healthz/live", () => Results.Ok("ok")).AllowAnonymous();
app.MapGet("/healthz/ready", async (AppDbContext db, IFileStore files) =>
{
    var dbOk = await db.Database.CanConnectAsync();
    var schemaOk = dbOk && await Startup.SchemaReadyAsync(db);
    var filesOk = await files.PingAsync();
    return dbOk && schemaOk && filesOk
        ? Results.Ok(new { db = "ok", schema = "ok", storage = "ok" })
        : Results.Json(new { db = dbOk, schema = schemaOk, storage = filesOk }, statusCode: 503);
}).AllowAnonymous();

var api = app.MapGroup("/api").RequireAuthorization();
AuthEndpoints.Map(api);
DefinitionEndpoints.Map(api);
ItemEndpoints.Map(api);
StockEndpoints.Map(api);
PeopleEndpoints.Map(api);
AssignmentEndpoints.Map(api);
DashboardEndpoints.Map(api);
TransferEndpoints.Map(api);
ViewEndpoints.Map(api);
DirectoryEndpoints.Map(api);
api.MapFallback(() => Results.NotFound(new Nurol.Envanter.Shared.ApiError("İstenen adres bulunamadı.")));

app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;

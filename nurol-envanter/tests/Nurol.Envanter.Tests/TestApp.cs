using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Shared;
using Testcontainers.PostgreSql;

namespace Nurol.Envanter.Tests;

/// <summary>
/// Gerçek PostgreSQL üzerinde çalışan uygulama. NE_TEST_DB tanımlıysa o veritabanı, değilse Testcontainers ile
/// geçici bir PostgreSQL konteyneri kullanılır. Örnek veri (DevSeeder) yüklenir.
/// </summary>
public sealed class TestApp : IAsyncLifetime
{
    PostgreSqlContainer? container;
    public string ConnectionString { get; private set; } = "";
    public string StoragePath { get; } = Path.Combine(Path.GetTempPath(), "ne-test-" + Guid.NewGuid().ToString("N"));
    public WebApplicationFactory<Program> Factory { get; private set; } = default!;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("NE_TEST_DB");
        if (!string.IsNullOrEmpty(external)) ConnectionString = external;
        else
        {
            container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
            await container.StartAsync();
            ConnectionString = container.GetConnectionString();
        }
        Factory = Create();
        // İlk istek uygulamayı başlatır: migration + örnek veri.
        using var c = Factory.CreateClient();
        (await c.GetAsync("/healthz/ready")).EnsureSuccessStatusCode();
    }

    /// <summary>Aynı veritabanına bağlı ayrı bir uygulama örneği ("ikinci pod").</summary>
    public WebApplicationFactory<Program> Create() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Development");
        b.UseSetting("ConnectionStrings:Default", ConnectionString);
        b.UseSetting("Storage:Provider", "FileSystem");
        b.UseSetting("Storage:Path", StoragePath);
        b.UseSetting("Seed:SampleData", "true");
        b.UseSetting("Database:MigrateOnStartup", "true");
        b.UseSetting("Security:LoginAttemptsPerMinute", "10000");
        b.UseSetting("Printers:Enabled", "false");      // testlerde arka plan okuması yok; okumalar elle tetiklenir
        b.UseSetting("Printers:TimeoutSeconds", "1");
    });

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (container is not null) await container.DisposeAsync();
        try { Directory.Delete(StoragePath, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public async Task<HttpClient> Login(string user, WebApplicationFactory<Program>? factory = null)
    {
        var c = (factory ?? Factory).CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-NE-Request", "1");
        var res = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(user, "Gelistirme123"));
        res.EnsureSuccessStatusCode();
        return c;
    }

    public T Db<T>(Func<AppDbContext, T> f)
    {
        using var scope = Factory.Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}

[CollectionDefinition("app")]
public class AppCollection : ICollectionFixture<TestApp>;

public static class Http
{
    public static async Task<T> Read<T>(this HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(TestApp.Json))!;
    public static Task<HttpResponseMessage> PostJson(this HttpClient c, string url, object body, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, body.GetType(), options: TestApp.Json) };
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        return c.SendAsync(req);
    }
    public static Task<HttpResponseMessage> PutJson(this HttpClient c, string url, object body) =>
        c.PutAsync(url, JsonContent.Create(body, body.GetType(), options: TestApp.Json));
    public static async Task<T> GetJson<T>(this HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        r.EnsureSuccessStatusCode();
        return await r.Read<T>();
    }
    public static MultipartFormDataContent File(byte[] bytes, string name, string type)
    {
        var content = new MultipartFormDataContent();
        var f = new ByteArrayContent(bytes);
        f.Headers.ContentType = new MediaTypeHeaderValue(type);
        content.Add(f, "file", name);
        return content;
    }
}

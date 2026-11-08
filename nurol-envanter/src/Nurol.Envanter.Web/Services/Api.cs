using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Web.Services;

public class ApiException(string message, Dictionary<string, string>? errors = null, int status = 0, JsonElement? body = null) : Exception(message)
{
    public Dictionary<string, string> Errors { get; } = errors ?? [];
    public int Status { get; } = status;
    public JsonElement? Body { get; } = body;
    public string? For(string key) => Errors.TryGetValue(key, out var v) ? v : null;
}

/// <summary>Sunucu API istemcisi. Durum değiştiren isteklere CSRF başlığı eklenir; 401'de giriş ekranına dönülür.</summary>
public class Api(HttpClient http, NavigationManager nav)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<T> Get<T>(string url) => await Send<T>(new HttpRequestMessage(HttpMethod.Get, url));

    public Task<T> Post<T>(string url, object? body, string? idempotencyKey = null) => Send<T>(Build(HttpMethod.Post, url, body, idempotencyKey));
    public Task Post(string url, object? body = null, string? idempotencyKey = null) => Send<object?>(Build(HttpMethod.Post, url, body, idempotencyKey));
    public Task<T> Put<T>(string url, object body) => Send<T>(Build(HttpMethod.Put, url, body, null));
    public Task Put(string url, object body) => Send<object?>(Build(HttpMethod.Put, url, body, null));
    public Task<T> Delete<T>(string url) => Send<T>(Build(HttpMethod.Delete, url, null, null));

    public Task<T> Upload<T>(string url, HttpContent content)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.Add("X-NE-Request", "1");
        return Send<T>(req);
    }

    static HttpRequestMessage Build(HttpMethod m, string url, object? body, string? key)
    {
        var req = new HttpRequestMessage(m, url);
        req.Headers.Add("X-NE-Request", "1");
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return req;
    }

    async Task<T> Send<T>(HttpRequestMessage req)
    {
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req);
        }
        catch (HttpRequestException)
        {
            throw new ApiException("Sunucuya ulaşılamadı. Bağlantınızı kontrol edip tekrar deneyin.");
        }
        if (res.StatusCode == HttpStatusCode.Unauthorized && !req.RequestUri!.ToString().Contains("auth/"))
        {
            nav.NavigateTo($"giris?donus={Uri.EscapeDataString(nav.ToBaseRelativePath(nav.Uri))}");
            throw new ApiException("Oturumunuz sona erdi. Lütfen tekrar giriş yapın.", status: 401);
        }
        if (!res.IsSuccessStatusCode)
        {
            JsonElement? body = null;
            string message = res.StatusCode switch
            {
                HttpStatusCode.Forbidden => "Bu işlem için yetkiniz yok.",
                HttpStatusCode.NotFound => "Kayıt bulunamadı.",
                _ => "İşlem tamamlanamadı."
            };
            Dictionary<string, string>? errors = null;
            try
            {
                var el = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
                body = el;
                if (el.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } s) message = s;
                if (el.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
                    errors = e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            }
            catch (JsonException) { }
            catch (NotSupportedException) { }
            throw new ApiException(message, errors, (int)res.StatusCode, body);
        }
        if (typeof(T) == typeof(object) || res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength == 0)
            return default!;
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }
}

/// <summary>Oturum açmış kullanıcının bilgisi ve yetki kısayolları.</summary>
public class Session(Api api)
{
    public MeDto? Me { get; private set; }
    public bool Loaded { get; private set; }
    public event Action? Changed;

    public bool CanWrite => Me?.CanWrite == true;
    public bool IsSystemAdmin => Me?.IsSystemAdmin == true;
    public bool CanManageDefinitions => Me?.Roles.Any(r => r is Roles.SystemAdmin or Roles.CompanyAdmin) == true;
    public string RoleLabel => Me is null ? "" : string.Join(", ", Me.Roles.Select(Roles.Label));

    public async Task<bool> EnsureAsync()
    {
        if (Loaded) return Me is not null;
        try { Me = await api.Get<MeDto>("api/auth/me"); }
        catch (ApiException) { Me = null; }
        Loaded = true;
        Changed?.Invoke();
        return Me is not null;
    }

    public void Set(MeDto me) { Me = me; Loaded = true; Changed?.Invoke(); }

    public async Task LogoutAsync()
    {
        try { await api.Post("api/auth/logout"); } catch (ApiException) { }
        Me = null; Loaded = true;
        Changed?.Invoke();
    }
}

public class Toasts
{
    public string? Message { get; private set; }
    public event Action? Changed;
    CancellationTokenSource? cts;

    public async void Show(string message)
    {
        cts?.Cancel();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        Message = message;
        Changed?.Invoke();
        try { await Task.Delay(3500, token); } catch (TaskCanceledException) { return; }
        Message = null;
        Changed?.Invoke();
    }
}

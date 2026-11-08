using Microsoft.EntityFrameworkCore;
using Npgsql;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Infrastructure;

/// <summary>Kullanıcıya anlaşılır Türkçe mesajla dönen iş kuralı hatası.</summary>
public class AppException(int status, string message, Dictionary<string, string>? errors = null) : Exception(message)
{
    public int Status { get; } = status;
    public Dictionary<string, string>? Errors { get; } = errors;

    public static AppException NotFound(string what = "Kayıt") => new(404, $"{what} bulunamadı veya bu kayda erişim yetkiniz yok.");
    public static AppException Forbidden(string message = "Bu işlem için yetkiniz yok.") => new(403, message);
    public static AppException Conflict(string message) => new(409, message);
    public static AppException Invalid(string message, Dictionary<string, string>? errors = null) => new(400, message, errors);
}

public class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> log)
{
    public async Task Invoke(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (AppException ex)
        {
            await Write(ctx, ex.Status, new ApiError(ex.Message, ex.Errors));
        }
        catch (DbUpdateConcurrencyException)
        {
            await Write(ctx, 409, new ApiError("Kayıt siz düzenlerken başka biri tarafından değiştirildi. Sayfayı yenileyip tekrar deneyin."));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            var (status, msg) = Translate(pg);
            if (status == 500) log.LogError(ex, "Veritabanı hatası");
            await Write(ctx, status, new ApiError(msg));
        }
        catch (PostgresException pg)
        {
            var (status, msg) = Translate(pg);
            if (status == 500) log.LogError(pg, "Veritabanı hatası");
            await Write(ctx, status, new ApiError(msg));
        }
        catch (BadHttpRequestException ex)
        {
            await Write(ctx, ex.StatusCode, new ApiError("İstek okunamadı. Gönderilen bilgileri kontrol edin."));
        }
    }

    internal static (int, string) Translate(PostgresException pg) => pg.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation => (409, pg.ConstraintName switch
        {
            "ux_assignment_lines_open_item" => "Seçilen cihazlardan biri bu sırada başka bir zimmete bağlandı.",
            "ix_items_manufacturer_id_serial" => "Bu üreticiye ait aynı seri numaralı bir cihaz zaten kayıtlı.",
            "pk_unique_field_values" => "Benzersiz olması gereken bir alan değeri başka bir kayıtta kullanılıyor.",
            _ => "Aynı bilgilerle kayıtlı başka bir kayıt var."
        }),
        PostgresErrorCodes.CheckViolation when pg.ConstraintName == "ck_stock_balance_non_negative"
            => (409, "Depodaki bakiye bu çıkış için yeterli değil."),
        PostgresErrorCodes.ForeignKeyViolation => (409, "Bu kayıt başka kayıtlarda kullanıldığı için işlem yapılamadı."),
        _ => (500, "Beklenmeyen bir veritabanı hatası oluştu.")
    };

    static Task Write(HttpContext ctx, int status, ApiError body)
    {
        if (ctx.Response.HasStarted) return Task.CompletedTask;
        ctx.Response.Clear();
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(body);
    }
}

public static class Idempotency
{
    public static bool IsKeyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg &&
        (pg.ConstraintName ?? "").Contains("idempotency_key", StringComparison.Ordinal);
}

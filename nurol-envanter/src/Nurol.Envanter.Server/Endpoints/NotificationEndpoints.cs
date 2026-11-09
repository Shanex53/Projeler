using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Server.Services;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

public static class NotificationEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // ---- Hesabım: kullanıcının kendi e-posta, bildirim ve (yerel hesapta) parola ayarı ----
        api.MapGet("/account", async (HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var u = await db.Users.FirstAsync(x => x.Id == a.UserId);
            return new MyAccountDto(u.UserName!, u.DisplayName, u.AccountType, u.Email, u.NotifyByEmail);
        });
        api.MapPut("/account", async (MyAccountSave req, HttpContext ctx, AppDbContext db, UserManager<AppUser> um) =>
        {
            var a = await ctx.Access();
            var u = await um.FindByIdAsync(a.UserId) ?? throw AppException.NotFound("Kullanıcı");
            var email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim().ToLowerInvariant();
            if (email is not null && (!email.Contains('@') || email.Contains(' ') || email.Length > 200))
                throw AppException.Invalid("Geçerli bir e-posta adresi girin.", new() { ["email"] = "Geçerli bir e-posta adresi girin." });
            u.Email = email; u.NormalizedEmail = email?.ToUpperInvariant(); u.NotifyByEmail = req.NotifyByEmail;
            var res = await um.UpdateAsync(u);
            if (!res.Succeeded) throw AppException.Invalid(string.Join(" ", res.Errors.Select(e => e.Description)));
            if (!string.IsNullOrEmpty(req.NewPassword))
            {
                if (u.AccountType != "Local") throw AppException.Invalid("AD hesabının parolası AD'de değiştirilir.");
                var pr = await um.ChangePasswordAsync(u, req.CurrentPassword ?? "", req.NewPassword);
                if (!pr.Succeeded)
                    throw AppException.Invalid(pr.Errors.Any(e => e.Code == "PasswordMismatch") ? "Mevcut parola hatalı." : "Yeni parola en az 10 karakter olmalı; büyük harf, küçük harf ve rakam içermelidir.",
                        new() { [pr.Errors.Any(e => e.Code == "PasswordMismatch") ? "current" : "new"] = "Kontrol edin." });
                Audit.Add(db, a, "Parola değişikliği", "Kullanıcı", u.Id, u.UserName);
                await db.SaveChangesAsync();
            }
            return Results.NoContent();
        });

        // ---- Bildirim yönetimi (sistem yöneticisi) ----
        api.MapGet("/notifications/status", async (HttpContext ctx, AppDbContext db, SmtpOptions o) =>
        {
            (await ctx.Access()).RequireSystemAdmin();
            var recent = await db.OutboxMails.OrderByDescending(m => m.CreatedAt).Take(30)
                .Select(m => new NotificationRow(m.Id, m.Kind, m.ToEmail, m.Subject, m.CreatedAt, m.SentAt, m.Attempts, m.LastError)).ToListAsync();
            return new NotificationStatusDto(o.Enabled, o.Host, o.Port, o.From, o.BaseUrl,
                await db.OutboxMails.CountAsync(m => m.SentAt == null && m.Attempts < o.MaxAttempts),
                await db.OutboxMails.CountAsync(m => m.SentAt == null && m.Attempts >= o.MaxAttempts), recent);
        });
        api.MapPost("/notifications/test", async (HttpContext ctx, AppDbContext db, Notifier n) =>
        {
            var a = await ctx.Access(); a.RequireSystemAdmin();
            var u = await db.Users.FirstAsync(x => x.Id == a.UserId);
            if (string.IsNullOrWhiteSpace(u.Email)) throw AppException.Invalid("Önce Hesabım sayfasından e-posta adresinizi girin.");
            n.Test(u.Email, u.DisplayName);
            await db.SaveChangesAsync();
            return Results.Ok(new { message = $"Test e-postası {u.Email} adresine sıraya alındı." });
        });
        api.MapPost("/notifications/digest", async (HttpContext ctx, Notifier n) =>
        {
            (await ctx.Access()).RequireSystemAdmin();
            var count = await n.DailyDigestAsync(DateOnly.FromDateTime(TimeZones.ToLocal(DateTimeOffset.UtcNow)));
            return Results.Ok(new { count, message = count == 0 ? "Bugün için yeni özet üretilmedi (bildirecek kayıt yok veya bugün zaten gönderildi)." : $"{count} kişiye günlük özet sıraya alındı." });
        });
        api.MapPost("/notifications/{id:guid}/retry", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            (await ctx.Access()).RequireSystemAdmin();
            var m = await db.OutboxMails.FindAsync(id) ?? throw AppException.NotFound("E-posta");
            if (m.SentAt is not null) throw AppException.Conflict("Bu e-posta zaten gönderildi.");
            m.Attempts = 0; m.SendAfter = DateTimeOffset.UtcNow; m.LastError = null;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}

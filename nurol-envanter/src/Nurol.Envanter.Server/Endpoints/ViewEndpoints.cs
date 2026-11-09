using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

/// <summary>
/// Kullanıcıya özel kayıtlı görünümler (filtre, sıralama, sütun düzeni). Görünüm yalnızca sahibine görünür.
/// Görünüm bir erişim kısıtı değildir; veriler her zaman sunucu yetkisiyle süzülür.
/// </summary>
public static class ViewEndpoints
{
    static readonly string[] Pages = ["envanter"];

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/views", async (string page, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            return await db.SavedViews.Where(v => v.UserId == a.UserId && v.Page == page).OrderBy(v => v.Name)
                .Select(v => new SavedViewDto(v.Id, v.Page, v.Name, v.State, v.UpdatedAt)).ToListAsync();
        });

        api.MapPost("/views", async (SavedViewSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            Validate(req);
            var name = req.Name.Trim();
            // Boş ad = sayfanın varsayılan düzeni; varsa güncellenir.
            var v = await db.SavedViews.FirstOrDefaultAsync(x => x.UserId == a.UserId && x.Page == req.Page && x.Name == name);
            if (v is not null && name.Length > 0) throw AppException.Conflict($"\"{name}\" adlı bir görünümünüz zaten var.");
            if (v is null)
            {
                if (await db.SavedViews.CountAsync(x => x.UserId == a.UserId && x.Page == req.Page) >= 30)
                    throw AppException.Conflict("En fazla 30 görünüm kaydedebilirsiniz. Kullanmadıklarınızı silin.");
                v = new SavedView { Id = Guid.NewGuid(), UserId = a.UserId, Page = req.Page, Name = name };
                db.SavedViews.Add(v);
            }
            v.State = req.State; v.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new SavedViewDto(v.Id, v.Page, v.Name, v.State, v.UpdatedAt));
        });

        api.MapPut("/views/{id:guid}", async (Guid id, SavedViewSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            Validate(req);
            var v = await db.SavedViews.FirstOrDefaultAsync(x => x.Id == id && x.UserId == a.UserId) ?? throw AppException.NotFound("Görünüm");
            var name = req.Name.Trim();
            if (name != v.Name && await db.SavedViews.AnyAsync(x => x.UserId == a.UserId && x.Page == v.Page && x.Name == name))
                throw AppException.Conflict($"\"{name}\" adlı bir görünümünüz zaten var.");
            v.Name = name; v.State = req.State; v.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new SavedViewDto(v.Id, v.Page, v.Name, v.State, v.UpdatedAt));
        });

        api.MapDelete("/views/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var n = await db.SavedViews.Where(x => x.Id == id && x.UserId == a.UserId).ExecuteDeleteAsync();
            return n == 0 ? throw AppException.NotFound("Görünüm") : Results.NoContent();
        });
    }

    static void Validate(SavedViewSave r)
    {
        if (!Pages.Contains(r.Page)) throw AppException.Invalid("Geçersiz sayfa.");
        if ((r.Name ?? "").Trim().Length > 80) throw AppException.Invalid("Görünüm adı en fazla 80 karakter olabilir.");
        if (r.State is null || r.State.Length > 20_000) throw AppException.Invalid("Görünüm ayarları geçersiz.");
        try { using var _ = JsonDocument.Parse(r.State); }
        catch (JsonException) { throw AppException.Invalid("Görünüm ayarları geçersiz."); }
    }
}

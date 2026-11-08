using System.Globalization;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Web.Services;

/// <summary>Tarih/sayı biçimleri. Sunucu UTC saklar; ekranda Europe/Istanbul gösterilir.</summary>
public static class Fmt
{
    public static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    static readonly TimeZoneInfo Istanbul = Find();

    static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("TR", TimeSpan.FromHours(3), "Türkiye", "Türkiye"); }
    }

    public static DateTime Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Istanbul).DateTime;
    public static string DateTime(DateTimeOffset utc) => Local(utc).ToString("dd.MM.yyyy HH:mm", Tr);
    public static string DateTime(DateTimeOffset? utc) => utc is null ? "—" : DateTime(utc.Value);
    public static string Date(DateTimeOffset utc) => Local(utc).ToString("dd.MM.yyyy", Tr);
    public static string Time(DateTimeOffset utc) => Local(utc).ToString("HH:mm", Tr);
    public static string Qty(decimal q) => q.ToString("#,##0.###", Tr);
    public static string Money(decimal? m) => m is null ? "—" : m.Value.ToString("#,##0.00", Tr) + " ₺";
    public static string Size(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB".Replace('.', ',') : $"{bytes / 1024.0 / 1024.0:0.#} MB".Replace('.', ',');
    public static string Ago(DateTimeOffset utc)
    {
        var d = System.DateTimeOffset.UtcNow - utc;
        if (d.TotalDays >= 1) return $"{(int)d.TotalDays} gün önce";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours} saat önce";
        return "az önce";
    }

    public static string FieldDisplay(FieldType t, string? v) => t switch
    {
        _ when string.IsNullOrEmpty(v) => "",
        FieldType.Boolean => v == "true" ? "Evet" : "Hayır",
        FieldType.Date => DateOnly.TryParseExact(v, "yyyy-MM-dd", out var d) ? d.ToString("dd.MM.yyyy") : v!,
        FieldType.Number => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n.ToString("#,##0.######", Tr) : v!,
        _ => v!
    };
}

using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;

namespace Nurol.Envanter.Server.Infrastructure;

/// <summary>Belge ve etiket numaraları. Tek SQL ifadesiyle atomik artırılır; çoklu pod'da çakışmaz.</summary>
public static class Counters
{
    public static async Task<long> NextAsync(AppDbContext db, string name, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<long>($"""
            INSERT INTO counters (name, value) VALUES ({name}, 1)
            ON CONFLICT (name) DO UPDATE SET value = counters.value + 1
            RETURNING value AS "Value"
            """).ToListAsync(ct);
        return rows[0];
    }

    public static async Task<string> DocumentNoAsync(AppDbContext db, string prefix, DateTimeOffset at, CancellationToken ct = default)
    {
        var year = TimeZones.ToLocal(at).Year;
        var n = await NextAsync(db, $"{prefix}-{year}", ct);
        return $"{prefix}-{year}-{n:000000}";
    }
}

public static class TimeZones
{
    public static readonly TimeZoneInfo Istanbul = Find();

    static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("TR", TimeSpan.FromHours(3), "Türkiye", "Türkiye"); }
    }

    public static DateTime ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Istanbul).DateTime;
    public static string Format(DateTimeOffset utc) => ToLocal(utc).ToString("dd.MM.yyyy HH:mm");
}

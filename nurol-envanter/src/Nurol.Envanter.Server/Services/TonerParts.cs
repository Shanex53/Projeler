using System.Text.RegularExpressions;
using Nurol.Envanter.Server.Data;

namespace Nurol.Envanter.Server.Services;

/// <summary>
/// Yazıcının bildirdiği sarf açıklamasından parça numarasını çıkarır ve stoktaki tonerle eşleştirir.
/// Kyocera açıklamaları doğrudan kodu verir (TK-1170, TK-5240K, DK-1150, WT-5140, MK-1150);
/// HP açıklamaları parça numarasını içerir ("Black Cartridge HP CF259A", "W2030X").
/// Eşleştirme yalnızca tek aday varsa öneri üretir; otomatik atama yapılmaz.
/// </summary>
public static partial class TonerParts
{
    [GeneratedRegex(@"\b(TK|DK|WT|MK|DV)\s?-?\s?(\d{3,5})([A-Z]{0,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Kyocera();

    [GeneratedRegex(@"\b(W|CF|CE|CB|CC|Q)(\d{3,5})([A-Z]{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Hp();

    public static List<string> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return Kyocera().Matches(text).Concat(Hp().Matches(text))
            .Select(m => (m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value).ToUpperInvariant())
            .Distinct().ToList();
    }

    /// <summary>Renk harfi olmadan (TK5240K → TK5240); “TK-5240” adlı genel bir stok kalemiyle eşleşebilmek için.</summary>
    static string Base(string part) => part.Length > 1 && "KCMY".Contains(part[^1]) && char.IsDigit(part[^2]) ? part[..^1] : part;

    public static string? Display(string? text) => Extract(text).FirstOrDefault() is { } p
        ? (p[..2] is "TK" or "DK" or "WT" or "MK" or "DV" ? p[..2] + "-" + p[2..] : p) : null;

    /// <summary>Yuva açıklamasına uyan tek stok kalemi (aynı şirketin, arşivlenmemiş); yoksa veya birden fazlaysa null.</summary>
    public static StockItem? Suggest(string? description, IEnumerable<StockItem> candidates)
    {
        var parts = Extract(description);
        if (parts.Count == 0) return null;
        var list = candidates.Select(s => (Item: s, Parts: Extract(s.Name + " " + s.Code))).Where(x => x.Parts.Count > 0).ToList();
        var exact = list.Where(x => x.Parts.Intersect(parts).Any()).Select(x => x.Item).Distinct().ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;
        var bases = parts.Select(Base).ToHashSet();
        var loose = list.Where(x => x.Parts.Select(Base).Any(bases.Contains)).Select(x => x.Item).Distinct().ToList();
        return loose.Count == 1 ? loose[0] : null;
    }
}

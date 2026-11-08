using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Services;

public static class FieldRules
{
    static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Bir kayda uygulanacak aktif alanlar: kategorinin varsayılan grubu + modelin ek alanları.</summary>
    public static Task<List<FieldDefinition>> ActiveFieldsAsync(AppDbContext db, Guid categoryId, Guid modelId, CancellationToken ct = default) =>
        db.FieldDefinitions.Where(f => !f.Archived && (f.CategoryId == categoryId || f.AssetModelId == modelId))
            .OrderBy(f => f.AssetModelId != null).ThenBy(f => f.SortOrder).ThenBy(f => f.Label).ToListAsync(ct);

    public record Parsed(string Text, decimal? Number, DateOnly? Date, bool? Bool);

    /// <summary>Değeri alan tipine göre doğrular ve normalize eder. Boş değer için null döner.</summary>
    public static Parsed? Parse(FieldDefinition f, string? raw, out string? error)
    {
        error = null;
        var v = raw?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            if (f.Required) error = $"{f.Label} zorunludur.";
            return null;
        }
        switch (f.Type)
        {
            case FieldType.Text:
                if (f.MaxLength is int max && v.Length > max) error = $"{f.Label} en fazla {max} karakter olabilir.";
                else if (f.DigitsOnly && !v.All(char.IsAsciiDigit)) error = $"{f.Label} yalnızca rakamlardan oluşmalıdır.";
                else if (f.ExactLength is int len && v.Length != len) error = $"{f.Label} tam {len} karakter olmalıdır (girilen: {v.Length}).";
                return error is null ? new Parsed(v, null, null, null) : null;
            case FieldType.Number:
                if (decimal.TryParse(v, NumberStyles.Number, Tr, out var n) || decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out n))
                    return new Parsed(n.ToString(CultureInfo.InvariantCulture), n, null, null);
                error = $"{f.Label} geçerli bir sayı olmalıdır.";
                return null;
            case FieldType.Date:
                if (DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ||
                    DateOnly.TryParseExact(v, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                    return new Parsed(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, d, null);
                error = $"{f.Label} geçerli bir tarih olmalıdır (gg.aa.yyyy).";
                return null;
            case FieldType.Select:
                var opt = f.Options.FirstOrDefault(o => string.Equals(o, v, StringComparison.CurrentCultureIgnoreCase));
                if (opt is null) { error = $"{f.Label} için listede olmayan değer: {v}."; return null; }
                return new Parsed(opt, null, null, null);
            case FieldType.Boolean:
                var b = v.ToLower(Tr) switch { "true" or "evet" or "1" => true, "false" or "hayır" or "0" => (bool?)false, _ => null };
                if (b is null) { error = $"{f.Label} evet veya hayır olmalıdır."; return null; }
                return new Parsed(b.Value ? "true" : "false", null, null, b);
        }
        return null;
    }

    public static string UniqueKey(string text) => text.Trim().ToUpperInvariant();

    public static string Display(FieldType type, string? text) => type switch
    {
        _ when text is null => "",
        FieldType.Boolean => text == "true" ? "Evet" : "Hayır",
        FieldType.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", out var d) ? d.ToString("dd.MM.yyyy") : text,
        FieldType.Number => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n.ToString("#,##0.######", Tr) : text,
        _ => text
    };

    /// <summary>
    /// Alan değerlerini doğrulayıp kayda uygular. Benzersiz alanlar UniqueFieldValue tablosuyla
    /// veritabanında da korunur. Hata varsa hiçbir şey değiştirilmez.
    /// </summary>
    public static async Task ApplyAsync(AppDbContext db, InventoryItem item, List<FieldDefinition> fields,
        IReadOnlyDictionary<Guid, string?> input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var parsed = new Dictionary<Guid, Parsed?>();
        foreach (var f in fields)
        {
            input.TryGetValue(f.Id, out var raw);
            var p = Parse(f, raw, out var err);
            if (err is not null) errors[f.Id.ToString()] = err;
            parsed[f.Id] = p;
        }

        var uniqueFields = fields.Where(f => f.Unique).ToList();
        foreach (var f in uniqueFields)
        {
            if (parsed[f.Id] is not { } p) continue;
            var key = UniqueKey(p.Text);
            var owner = await db.UniqueFieldValues.Where(u => u.FieldId == f.Id && u.Value == key && u.ItemId != item.Id)
                .Join(db.Items, u => u.ItemId, i => i.Id, (u, i) => i.Tag).FirstOrDefaultAsync(ct);
            if (owner is not null) errors[f.Id.ToString()] = $"{f.Label} değeri {p.Text} başka bir kayıtta ({owner}) kullanılıyor.";
        }
        if (errors.Count > 0) throw AppException.Invalid("Bazı alanlar hatalı. Lütfen işaretli alanları düzeltin.", errors);

        var fieldIds = fields.Select(f => f.Id).ToList();
        var existing = item.Id == Guid.Empty ? [] :
            await db.FieldValues.Where(v => v.ItemId == item.Id && fieldIds.Contains(v.FieldId)).ToListAsync(ct);
        foreach (var f in fields)
        {
            var row = existing.FirstOrDefault(v => v.FieldId == f.Id);
            var p = parsed[f.Id];
            if (p is null)
            {
                if (row is not null) db.FieldValues.Remove(row);
                continue;
            }
            if (row is null)
            {
                row = new FieldValue { ItemId = item.Id, FieldId = f.Id };
                db.FieldValues.Add(row);
            }
            row.Text = p.Text; row.Number = p.Number; row.Date = p.Date; row.Bool = p.Bool;
        }

        if (uniqueFields.Count > 0)
        {
            var uIds = uniqueFields.Select(f => f.Id).ToList();
            await db.UniqueFieldValues.Where(u => u.ItemId == item.Id && uIds.Contains(u.FieldId)).ExecuteDeleteAsync(ct);
            foreach (var f in uniqueFields)
                if (parsed[f.Id] is { } p)
                    db.UniqueFieldValues.Add(new UniqueFieldValue { FieldId = f.Id, Value = UniqueKey(p.Text), ItemId = item.Id });
        }
    }

    /// <summary>Etiketten kararlı alan anahtarı üretir (ör. "IMEI 1" → "imei_1").</summary>
    public static string Slug(string label)
    {
        var map = new Dictionary<char, char> { ['ç'] = 'c', ['ğ'] = 'g', ['ı'] = 'i', ['ö'] = 'o', ['ş'] = 's', ['ü'] = 'u', ['İ'] = 'i' };
        var sb = new StringBuilder();
        foreach (var ch in label.Trim().ToLower(Tr))
        {
            var c = map.TryGetValue(ch, out var m) ? m : ch;
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var s = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(s) ? "alan" : s;
    }

    /// <summary>
    /// Kural değişikliğinin mevcut verilerle çelişkisini bulur. Sessiz dönüşüm yapılmaz; çelişki varsa
    /// kural kaydedilmez ve yöneticiye çelişen kayıtlar gösterilir.
    /// </summary>
    public static async Task<List<FieldConflictDto>> ConflictsAsync(AppDbContext db, FieldDefinition current, FieldDefinitionSave next, CancellationToken ct = default)
    {
        var result = new List<FieldConflictDto>();
        var scopeItems = db.Items.Where(i => current.CategoryId != null ? i.CategoryId == current.CategoryId : i.AssetModelId == current.AssetModelId);
        var values = db.FieldValues.Where(v => v.FieldId == current.Id);

        if (next.Required && !current.Required)
        {
            var missing = await scopeItems.Where(i => !values.Any(v => v.ItemId == i.Id)).OrderBy(i => i.Tag).Select(i => i.Tag).Take(200).ToListAsync(ct);
            if (missing.Count > 0) result.Add(new FieldConflictDto("Zorunlu: değeri boş olan kayıtlar", missing));
        }
        if (next.Unique && !current.Unique)
        {
            var rows = await values.Join(db.Items, v => v.ItemId, i => i.Id, (v, i) => new { v.Text, i.Tag }).ToListAsync(ct);
            var dup = rows.Where(r => r.Text != null).GroupBy(r => UniqueKey(r.Text!)).Where(g => g.Count() > 1).SelectMany(g => g.Select(x => x.Tag)).OrderBy(x => x).ToList();
            if (dup.Count > 0) result.Add(new FieldConflictDto("Benzersiz: aynı değeri taşıyan kayıtlar", dup));
        }
        if (current.Type == FieldType.Text && next.Type == FieldType.Text)
        {
            var probe = new FieldDefinition { Label = current.Label, Type = FieldType.Text, MaxLength = next.MaxLength, DigitsOnly = next.DigitsOnly, ExactLength = next.ExactLength };
            var rows = await values.Join(db.Items, v => v.ItemId, i => i.Id, (v, i) => new { v.Text, i.Tag }).ToListAsync(ct);
            var bad = rows.Where(r => Parse(probe, r.Text, out var e) is null && e is not null).Select(r => r.Tag).OrderBy(x => x).ToList();
            if (bad.Count > 0) result.Add(new FieldConflictDto("Biçim: yeni uzunluk/rakam kuralına uymayan kayıtlar", bad));
        }
        if (current.Type == FieldType.Select && next.Options is not null)
        {
            var allowed = next.Options.Select(o => o.Trim()).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            var rows = await values.Join(db.Items, v => v.ItemId, i => i.Id, (v, i) => new { v.Text, i.Tag }).ToListAsync(ct);
            var bad = rows.Where(r => r.Text != null && !allowed.Contains(r.Text)).Select(r => r.Tag).OrderBy(x => x).ToList();
            if (bad.Count > 0) result.Add(new FieldConflictDto("Seçim listesi: kaldırılan seçeneği kullanan kayıtlar", bad));
        }
        return result;
    }
}

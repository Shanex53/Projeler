using ClosedXML.Excel;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Infrastructure;

/// <summary>Rapor tablolarını tipli hücrelerle Excel'e yazar (tarih, para, sayı biçimleri korunur).</summary>
public static class Excel
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] Write(ReportTable t)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SheetName(t.Title));
        ws.Cell(1, 1).Value = t.Title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        var r = 2;
        ws.Cell(r, 1).Value = $"Oluşturma: {TimeZones.Format(DateTimeOffset.UtcNow)} (İstanbul)";
        ws.Cell(r, 1).Style.Font.FontColor = XLColor.Gray;
        if (!string.IsNullOrEmpty(t.Note)) { r++; ws.Cell(r, 1).Value = t.Note; ws.Cell(r, 1).Style.Font.FontColor = XLColor.Gray; }
        foreach (var s in t.Summary) { r++; ws.Cell(r, 1).Value = s.Label; ws.Cell(r, 2).Value = s.Value; ws.Cell(r, 1).Style.Font.Bold = true; }
        r += 2;
        var header = r;
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var cell = ws.Cell(header, c + 1);
            cell.Value = t.Columns[c].Label;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E6EEFC");
        }
        foreach (var row in t.Rows)
        {
            r++;
            for (var c = 0; c < t.Columns.Count && c < row.Length; c++) Set(ws.Cell(r, c + 1), t.Columns[c].Type, row[c]);
        }
        if (t.Rows.Count > 0) ws.Range(header, 1, r, t.Columns.Count).SetAutoFilter();
        ws.SheetView.FreezeRows(header);
        ws.Columns(1, t.Columns.Count).AdjustToContents(header, r, 8, 60);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    static void Set(IXLCell cell, string type, object? v)
    {
        switch (v)
        {
            case null: return;
            case DateTimeOffset d:
                cell.Value = TimeZones.ToLocal(d);
                cell.Style.DateFormat.Format = type == "date" ? "dd.MM.yyyy" : "dd.MM.yyyy HH:mm";
                return;
            case DateOnly d: cell.Value = d.ToDateTime(TimeOnly.MinValue); cell.Style.DateFormat.Format = "dd.MM.yyyy"; return;
            case decimal m: cell.Value = m; cell.Style.NumberFormat.Format = type == "money" ? "#,##0.00 ₺" : "#,##0.###"; return;
            case int i: cell.Value = i; return;
            case long l: cell.Value = l; return;
            case bool b: cell.Value = b ? "Evet" : "Hayır"; return;
            default: cell.Value = v.ToString(); return;
        }
    }

    /// <summary>Excel sayfa adı kuralları: en fazla 31 karakter, : \ / ? * [ ] yok.</summary>
    public static string SheetName(string s)
    {
        var clean = new string(s.Where(ch => ":\\/?*[]".IndexOf(ch) < 0).ToArray()).Trim();
        return clean.Length > 31 ? clean[..31] : clean.Length == 0 ? "Rapor" : clean;
    }

    public static string FileName(string title) =>
        new string(title.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-').ToLowerInvariant() + $"-{DateTime.UtcNow:yyyyMMdd}.xlsx";
}

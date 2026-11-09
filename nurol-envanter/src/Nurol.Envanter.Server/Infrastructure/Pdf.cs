using System.Reflection;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using Nurol.Envanter.Shared;
using PdfSharp.Fonts;

namespace Nurol.Envanter.Server.Infrastructure;

/// <summary>Zimmet/iade tutanağının işlem anındaki verileri. JSON olarak saklanır ve sonradan değişmez.</summary>
public record AssignmentSnapshot(
    string No, AssignmentKind Kind, string LocalTime, string CompanyName, string WarehouseName,
    string EmployeeName, string? RegistryNo, string? Title, string? Department,
    string IssuedBy, string? Note, List<SnapshotLine> Lines);

public record SnapshotLine(string Tag, string Description, string? Serial, string? Extra, string? Condition);

/// <summary>MIT lisanslı PDFsharp/MigraDoc ile A4 tutanak. Türkçe karakterler için DejaVu Sans gömülür.</summary>
public static class AssignmentPdf
{
    static readonly object Gate = new();
    static bool initialized;

    static void EnsureFonts()
    {
        lock (Gate)
        {
            if (initialized) return;
            GlobalFontSettings.FontResolver = new EmbeddedFontResolver();
            initialized = true;
        }
    }

    public static void EnsureFontsPublic() => EnsureFonts();

    public static byte[] Render(AssignmentSnapshot s)
    {
        EnsureFonts();
        var issue = s.Kind == AssignmentKind.Issue;
        var doc = new Document();
        doc.Info.Title = $"{(issue ? "Zimmet" : "İade")} tutanağı {s.No}";
        doc.Info.Author = "Nurol Envanter";

        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = EmbeddedFontResolver.Family;
        normal.Font.Size = 9.5;

        var sec = doc.AddSection();
        sec.PageSetup = doc.DefaultPageSetup.Clone();
        sec.PageSetup.PageFormat = PageFormat.A4;
        sec.PageSetup.TopMargin = Unit.FromCentimeter(1.8);
        sec.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        sec.PageSetup.LeftMargin = Unit.FromCentimeter(2);
        sec.PageSetup.RightMargin = Unit.FromCentimeter(2);

        var navy = new Color(0x1f, 0x3d, 0x66);
        var grid = new Color(0xcd, 0xd6, 0xe3);

        var head = sec.AddTable();
        head.AddColumn(Unit.FromCentimeter(11));
        head.AddColumn(Unit.FromCentimeter(6));
        var hr = head.AddRow();
        var title = hr.Cells[0].AddParagraph(issue ? "DEMİRBAŞ ZİMMET TUTANAĞI" : "DEMİRBAŞ İADE TUTANAĞI");
        title.Format.Font.Size = 15; title.Format.Font.Bold = true; title.Format.Font.Color = navy;
        hr.Cells[0].AddParagraph(s.CompanyName);
        var no = hr.Cells[1].AddParagraph(s.No);
        no.Format.Alignment = ParagraphAlignment.Right; no.Format.Font.Bold = true; no.Format.Font.Size = 11;
        hr.Cells[1].AddParagraph(s.LocalTime).Format.Alignment = ParagraphAlignment.Right;
        hr.Borders.Bottom.Width = 1.5; hr.Borders.Bottom.Color = navy;
        hr.BottomPadding = Unit.FromPoint(6);

        sec.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);
        var meta = sec.AddTable();
        meta.AddColumn(Unit.FromCentimeter(8.5));
        meta.AddColumn(Unit.FromCentimeter(8.5));
        void Pair(string l1, string? v1, string l2, string? v2)
        {
            var r = meta.AddRow();
            r.TopPadding = Unit.FromPoint(1.5);
            Label(r.Cells[0].AddParagraph(), l1, v1);
            Label(r.Cells[1].AddParagraph(), l2, v2);
        }
        Pair(issue ? "Teslim alan" : "İade eden", s.EmployeeName, "Sicil no", s.RegistryNo);
        Pair("Unvan", s.Title, "Birim", s.Department);
        Pair(issue ? "Teslim eden" : "Teslim alan", s.IssuedBy, "Depo", s.WarehouseName);

        sec.AddParagraph().Format.SpaceAfter = Unit.FromPoint(8);
        var t = sec.AddTable();
        t.Borders.Width = 0.5; t.Borders.Color = grid;
        t.Format.Font.Size = 8.5;
        foreach (var w in new[] { 0.6, 3.0, 3.5, 2.4, 5.2, 2.3 }) t.AddColumn(Unit.FromCentimeter(w));
        var th = t.AddRow();
        th.HeadingFormat = true; th.Shading.Color = new Color(0xee, 0xf2, 0xf8); th.Format.Font.Bold = true;
        string[] heads = ["#", "Envanter etiketi", "Cihaz", "Seri no", "Ek bilgiler", "Durum"];
        for (var i = 0; i < heads.Length; i++) th.Cells[i].AddParagraph(heads[i]);
        var n = 1;
        foreach (var l in s.Lines)
        {
            var r = t.AddRow();
            r.TopPadding = r.BottomPadding = Unit.FromPoint(2);
            r.Cells[0].AddParagraph((n++).ToString());
            r.Cells[1].AddParagraph(l.Tag);
            r.Cells[2].AddParagraph(l.Description);
            r.Cells[3].AddParagraph(l.Serial ?? "—");
            r.Cells[4].AddParagraph(l.Extra ?? "—");
            r.Cells[5].AddParagraph(l.Condition ?? (issue ? "Sağlam" : "—"));
        }

        var stmt = sec.AddParagraph(issue
            ? "Yukarıda bilgileri yazılı demirbaşları sağlam ve eksiksiz olarak teslim aldım. Görevim süresince özenle kullanacağımı, görevden ayrılmam veya talep edilmesi hâlinde eksiksiz iade edeceğimi kabul ederim."
            : "Yukarıda bilgileri yazılı demirbaşlar belirtilen durumlarıyla iade alınmıştır. İade eden personelin bu demirbaşlarla ilgili zimmeti kapatılmıştır.");
        stmt.Format.SpaceBefore = Unit.FromPoint(12);
        if (!string.IsNullOrWhiteSpace(s.Note))
        {
            var note = sec.AddParagraph($"Açıklama: {s.Note}");
            note.Format.SpaceBefore = Unit.FromPoint(6);
        }

        sec.AddParagraph().Format.SpaceAfter = Unit.FromPoint(30);
        var sig = sec.AddTable();
        sig.AddColumn(Unit.FromCentimeter(7.5));
        sig.AddColumn(Unit.FromCentimeter(2));
        sig.AddColumn(Unit.FromCentimeter(7.5));
        var names = sig.AddRow();
        void Who(Cell c, string role, string name)
        {
            c.Format.Alignment = ParagraphAlignment.Center;
            c.AddParagraph(role).Format.Font.Color = new Color(0x5b, 0x68, 0x79);
            c.AddParagraph(name).Format.Font.Bold = true;
        }
        Who(names.Cells[0], "Teslim eden", issue ? s.IssuedBy : s.EmployeeName);
        Who(names.Cells[2], "Teslim alan", issue ? s.EmployeeName : s.IssuedBy);
        var space = sig.AddRow();
        space.Height = Unit.FromCentimeter(1.8);
        space.HeightRule = RowHeightRule.AtLeast;
        var line = sig.AddRow();
        foreach (var i in new[] { 0, 2 })
        {
            line.Cells[i].Borders.Top.Width = 0.8;
            line.Cells[i].Format.Alignment = ParagraphAlignment.Center;
            line.Cells[i].AddParagraph("İmza / Tarih");
        }

        var footer = sec.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7.5; footer.Format.Font.Color = new Color(0x7a, 0x86, 0x96);
        footer.AddText($"{s.No} · Belge işlem anındaki verilerle üretilmiştir · Sayfa ");
        footer.AddPageField();
        footer.AddText(" / ");
        footer.AddNumPagesField();

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    static void Label(Paragraph p, string label, string? value)
    {
        p.AddFormattedText(label + ": ", new Font { Color = new Color(0x5b, 0x68, 0x79) });
        p.AddFormattedText(string.IsNullOrWhiteSpace(value) ? "—" : value, TextFormat.Bold);
    }
}

public class EmbeddedFontResolver : IFontResolver
{
    public const string Family = "DejaVu Sans";

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? "DejaVuSans-Bold" : "DejaVuSans");

    public byte[] GetFont(string faceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith($".{faceName}.ttf", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

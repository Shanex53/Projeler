using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;

namespace Nurol.Envanter.Server.Infrastructure;

/// <summary>İlişik kesme belgesinin kapanış anındaki verileri. JSON olarak saklanmaz; PDF dondurulmuş kopyadır.</summary>
public record OffboardingSnapshot(
    string No, string CompanyName, string EmployeeName, string? RegistryNo, string? Title, string? Department,
    string? LastWorkingDay, string OpenedAt, string CompletedAt, string CompletedBy, string Source,
    List<(string Kind, string Reference, string Text, string At)> Returned, List<(string Title, string? By, string? At)> Tasks, string? Note);

public static class OffboardingPdf
{
    public static byte[] Render(OffboardingSnapshot s)
    {
        AssignmentPdf.EnsureFontsPublic();
        var doc = new Document();
        doc.Info.Title = $"İlişik kesme belgesi {s.No}";
        doc.Info.Author = "Nurol Envanter";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = EmbeddedFontResolver.Family;
        normal.Font.Size = 9.5;

        var sec = doc.AddSection();
        sec.PageSetup = doc.DefaultPageSetup.Clone();
        sec.PageSetup.PageFormat = PageFormat.A4;
        sec.PageSetup.TopMargin = Unit.FromCentimeter(1.8);
        sec.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        sec.PageSetup.LeftMargin = sec.PageSetup.RightMargin = Unit.FromCentimeter(2);
        var navy = new Color(0x1f, 0x3d, 0x66);
        var grey = new Color(0x5b, 0x68, 0x79);
        var grid = new Color(0xcd, 0xd6, 0xe3);

        var head = sec.AddTable();
        head.AddColumn(Unit.FromCentimeter(11));
        head.AddColumn(Unit.FromCentimeter(6));
        var hr = head.AddRow();
        var title = hr.Cells[0].AddParagraph("İLİŞİK KESME BELGESİ");
        title.Format.Font.Size = 15; title.Format.Font.Bold = true; title.Format.Font.Color = navy;
        hr.Cells[0].AddParagraph(s.CompanyName);
        var no = hr.Cells[1].AddParagraph(s.No);
        no.Format.Alignment = ParagraphAlignment.Right; no.Format.Font.Bold = true; no.Format.Font.Size = 11;
        hr.Cells[1].AddParagraph(s.CompletedAt).Format.Alignment = ParagraphAlignment.Right;
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
            Label(r.Cells[0].AddParagraph(), l1, v1, grey);
            Label(r.Cells[1].AddParagraph(), l2, v2, grey);
        }
        Pair("Personel", s.EmployeeName, "Sicil no", s.RegistryNo);
        Pair("Unvan", s.Title, "Birim", s.Department);
        Pair("Son çalışma günü", s.LastWorkingDay, "Süreç başlangıcı", $"{s.OpenedAt} ({s.Source})");

        Heading(sec, "İade alınan cihaz ve hatlar", navy);
        if (s.Returned.Count == 0)
            sec.AddParagraph("Süreç başladığında personelin üzerinde cihaz veya hat bulunmuyordu.").Format.Font.Color = grey;
        else
        {
            var t = sec.AddTable();
            t.Borders.Width = 0.5; t.Borders.Color = grid; t.Format.Font.Size = 8.5;
            foreach (var w in new[] { 0.6, 1.4, 3.3, 8.7, 3.0 }) t.AddColumn(Unit.FromCentimeter(w));
            var th = t.AddRow();
            th.HeadingFormat = true; th.Shading.Color = new Color(0xee, 0xf2, 0xf8); th.Format.Font.Bold = true;
            string[] heads = ["#", "Tür", "Belge / numara", "Açıklama", "Tarih"];
            for (var i = 0; i < heads.Length; i++) th.Cells[i].AddParagraph(heads[i]);
            var n = 1;
            foreach (var r in s.Returned)
            {
                var row = t.AddRow();
                row.TopPadding = row.BottomPadding = Unit.FromPoint(2);
                row.Cells[0].AddParagraph((n++).ToString());
                row.Cells[1].AddParagraph(r.Kind);
                row.Cells[2].AddParagraph(r.Reference);
                row.Cells[3].AddParagraph(r.Text);
                row.Cells[4].AddParagraph(r.At);
            }
        }

        if (s.Tasks.Count > 0)
        {
            Heading(sec, "Tamamlanan görevler", navy);
            foreach (var t in s.Tasks)
            {
                var p = sec.AddParagraph();
                p.AddFormattedText("✓ ", TextFormat.Bold);
                p.AddText(t.Title);
                if (t.By is not null) p.AddFormattedText($"  ·  {t.By}, {t.At}", new Font { Color = grey, Size = 8 });
            }
        }

        var stmt = sec.AddParagraph($"{s.EmployeeName} adlı personelin şirketimize ait demirbaş, hat ve diğer teslim yükümlülükleri yukarıda belirtildiği şekilde tamamlanmış olup bu konudaki ilişiği kesilmiştir.");
        stmt.Format.SpaceBefore = Unit.FromPoint(14);
        if (!string.IsNullOrWhiteSpace(s.Note)) sec.AddParagraph($"Açıklama: {s.Note}").Format.SpaceBefore = Unit.FromPoint(6);

        sec.AddParagraph().Format.SpaceAfter = Unit.FromPoint(30);
        var sig = sec.AddTable();
        sig.AddColumn(Unit.FromCentimeter(7.5));
        sig.AddColumn(Unit.FromCentimeter(2));
        sig.AddColumn(Unit.FromCentimeter(7.5));
        var names = sig.AddRow();
        void Who(MigraDoc.DocumentObjectModel.Tables.Cell c, string role, string name)
        {
            c.Format.Alignment = ParagraphAlignment.Center;
            c.AddParagraph(role).Format.Font.Color = grey;
            c.AddParagraph(name).Format.Font.Bold = true;
        }
        Who(names.Cells[0], "Personel", s.EmployeeName);
        Who(names.Cells[2], "Onaylayan", s.CompletedBy);
        var space = sig.AddRow();
        space.Height = Unit.FromCentimeter(1.8);
        space.HeightRule = MigraDoc.DocumentObjectModel.Tables.RowHeightRule.AtLeast;
        var line = sig.AddRow();
        foreach (var i in new[] { 0, 2 })
        {
            line.Cells[i].Borders.Top.Width = 0.8;
            line.Cells[i].Format.Alignment = ParagraphAlignment.Center;
            line.Cells[i].AddParagraph("İmza / Tarih");
        }

        var footer = sec.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7.5; footer.Format.Font.Color = new Color(0x7a, 0x86, 0x96);
        footer.AddText($"{s.No} · Belge kapanış anındaki verilerle üretilmiştir · Sayfa ");
        footer.AddPageField();
        footer.AddText(" / ");
        footer.AddNumPagesField();

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    static void Heading(Section sec, string text, Color color)
    {
        var p = sec.AddParagraph(text);
        p.Format.SpaceBefore = Unit.FromPoint(14); p.Format.SpaceAfter = Unit.FromPoint(4);
        p.Format.Font.Bold = true; p.Format.Font.Size = 10.5; p.Format.Font.Color = color;
    }

    static void Label(Paragraph p, string label, string? value, Color grey)
    {
        p.AddFormattedText(label + ": ", new Font { Color = grey });
        p.AddFormattedText(string.IsNullOrWhiteSpace(value) ? "—" : value, TextFormat.Bold);
    }
}

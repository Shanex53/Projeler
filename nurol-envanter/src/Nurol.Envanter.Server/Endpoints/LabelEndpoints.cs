using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;
using QRCoder;

namespace Nurol.Envanter.Server.Endpoints;

/// <summary>
/// QR etiketleri ve kod okutma. QR içeriği yalnızca envanter etiketidir (ör. NRL-TLF-000214); sunucu adresi içermez,
/// böylece adres değişse de etiketler geçerli kalır.
/// </summary>
public static class LabelEndpoints
{
    public record Layout(string Key, string Name, int Cols, int Rows, double WidthMm, double HeightMm, double TopMm, double LeftMm);

    public static readonly Layout[] Layouts =
    [
        new("a4-3x8", "A4 · 24 etiket (70 × 37 mm)", 3, 8, 70, 37.125, 0, 0),
        new("a4-2x7", "A4 · 14 etiket (99 × 38 mm)", 2, 7, 99.1, 38.1, 15.15, 4.65),
    ];

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/labels/layouts", () => Layouts.Select(l => new { l.Key, l.Name, Count = l.Cols * l.Rows }));

        // Filtreler envanter listesiyle aynıdır; ids verilirse yalnızca o kayıtlar basılır.
        api.MapGet("/labels", async (HttpContext ctx, AppDbContext db, string? ids, string? layout, int? start,
            string? q, Guid? categoryId, Guid? warehouseId, ItemStatus? status) =>
        {
            var a = await ctx.Access();
            var l = Layouts.FirstOrDefault(x => x.Key == layout) ?? Layouts[0];
            var perPage = l.Cols * l.Rows;
            var skip = Math.Clamp((start ?? 1) - 1, 0, perPage - 1);
            List<Guid> idList;
            if (!string.IsNullOrWhiteSpace(ids))
                idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
            else
            {
                var (rows, total) = await ItemEndpoints.ListAsync(db, a, new ItemQuery(q, categoryId, warehouseId, status, null, null, null, "tag", false, 1, 1000));
                if (total > 1000) throw AppException.Invalid($"Filtre {total} kayıt döndürüyor; tek seferde en fazla 1000 etiket basılabilir. Filtreyi daraltın.");
                idList = rows.Select(r => r.Id).ToList();
            }
            var items = await a.Items(db).Where(i => idList.Contains(i.Id)).OrderBy(i => i.Tag)
                .Select(i => new LabelData(i.Tag, i.AssetModel!.Manufacturer!.Name + " " + i.AssetModel.Name, i.Serial, i.Company!.Name)).ToListAsync();
            if (items.Count == 0) throw AppException.NotFound("Basılacak envanter");
            Audit.Add(db, a, "Etiket basımı", "Envanter", null, $"{items.Count} etiket · {l.Key}");
            await db.SaveChangesAsync();
            return Results.File(Render(items, l, skip), "application/pdf", items.Count == 1 ? $"etiket-{items[0].Tag}.pdf" : $"etiketler-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf");
        });

        // El tipi okuyucu, kamera veya elle giriş: önce etiket, sonra seri no, sonra benzersiz özel alan (ör. IMEI).
        api.MapGet("/items/lookup", async (string code, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var c = (code ?? "").Trim();
            if (c.Length == 0) throw AppException.Invalid("Kod boş.");
            var items = a.Items(db);
            var byTag = await items.Where(i => i.Tag == c.ToUpperInvariant()).Select(i => new { i.Id, i.Tag }).FirstOrDefaultAsync();
            if (byTag is not null) return new LookupResult(byTag.Id, byTag.Tag, "Envanter etiketi");
            var bySerial = await items.Where(i => i.Serial == c.ToUpperInvariant()).Select(i => new { i.Id, i.Tag }).Take(2).ToListAsync();
            if (bySerial.Count == 1) return new LookupResult(bySerial[0].Id, bySerial[0].Tag, "Seri no");
            var key = c.ToUpperInvariant();
            var byField = await db.UniqueFieldValues.Where(u => u.Value == key).Join(items, u => u.ItemId, i => i.Id, (u, i) => new { i.Id, i.Tag, u.FieldId })
                .Take(2).ToListAsync();
            if (byField.Count == 1)
            {
                var label = await db.FieldDefinitions.Where(f => f.Id == byField[0].FieldId).Select(f => f.Label).FirstAsync();
                return new LookupResult(byField[0].Id, byField[0].Tag, label);
            }
            throw AppException.NotFound($"“{c}” koduyla eşleşen cihaz");
        });
    }

    public record LabelData(string Tag, string Model, string? Serial, string Company);

    public static byte[] Render(IReadOnlyList<LabelData> items, Layout l, int skip)
    {
        AssignmentPdf.EnsureFontsPublic();
        var doc = new MigraDoc.DocumentObjectModel.Document();
        doc.Info.Title = "Envanter etiketleri";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = EmbeddedFontResolver.Family;
        normal.Font.Size = 7.5;
        var perPage = l.Cols * l.Rows;
        var slots = skip + items.Count;
        var pages = (int)Math.Ceiling(slots / (double)perPage);
        using var qr = new QRCodeGenerator();
        var idx = 0;
        for (var p = 0; p < pages; p++)
        {
            var sec = doc.AddSection();
            sec.PageSetup = doc.DefaultPageSetup.Clone();
            sec.PageSetup.PageFormat = PageFormat.A4;
            sec.PageSetup.TopMargin = Unit.FromMillimeter(l.TopMm);
            sec.PageSetup.LeftMargin = Unit.FromMillimeter(l.LeftMm);
            sec.PageSetup.RightMargin = Unit.FromMillimeter(0);
            sec.PageSetup.BottomMargin = Unit.FromMillimeter(0);
            var t = sec.AddTable();
            t.Borders.Visible = false;
            for (var c = 0; c < l.Cols; c++) t.AddColumn(Unit.FromMillimeter(l.WidthMm));
            for (var r = 0; r < l.Rows; r++)
            {
                var row = t.AddRow();
                row.Height = Unit.FromMillimeter(l.HeightMm);
                row.HeightRule = RowHeightRule.Exactly;
                for (var c = 0; c < l.Cols; c++)
                {
                    var slot = p * perPage + r * l.Cols + c;
                    if (slot < skip || idx >= items.Count) continue;
                    Fill(row.Cells[c], items[idx++], l, qr);
                }
            }
        }
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    static void Fill(Cell cell, LabelData d, Layout l, QRCodeGenerator gen)
    {
        var qrSize = Math.Min(l.HeightMm - 6, 30);
        var inner = cell.Elements.AddTable();
        inner.Borders.Visible = false;
        inner.AddColumn(Unit.FromMillimeter(qrSize + 3));
        inner.AddColumn(Unit.FromMillimeter(l.WidthMm - qrSize - 6));
        var row = inner.AddRow();
        row.TopPadding = Unit.FromMillimeter(3);
        using var data = gen.CreateQrCode(d.Tag, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var img = row.Cells[0].AddImage("base64:" + Convert.ToBase64String(png));
        img.Width = Unit.FromMillimeter(qrSize);
        img.Height = Unit.FromMillimeter(qrSize);
        img.Left = Unit.FromMillimeter(2.5);
        var text = row.Cells[1];
        text.VerticalAlignment = VerticalAlignment.Center;
        var tag = text.AddParagraph(d.Tag);
        tag.Format.Font.Bold = true;
        tag.Format.Font.Size = 9;
        tag.Format.SpaceAfter = Unit.FromPoint(2);
        text.AddParagraph(Clip(d.Model, 34));
        if (d.Serial is not null) text.AddParagraph("SN: " + Clip(d.Serial, 26));
        var co = text.AddParagraph(Clip(d.Company, 30));
        co.Format.Font.Size = 6.5;
        co.Format.Font.Color = new Color(0x5b, 0x68, 0x79);
        co.Format.SpaceBefore = Unit.FromPoint(2);
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}

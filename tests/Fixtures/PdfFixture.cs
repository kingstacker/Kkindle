using System.Globalization;
using System.Text;

namespace Kkindle.TestFixtures;

/// <summary>A deterministic PDF with text, a scan, nested outlines and a rotated crop.</summary>
internal static class PdfFixture
{
    public static string WriteTable(string directory, bool filledRules = true, bool form = false)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"PDF table-{filledRules}-{form}.pdf");
        var content = new StringBuilder("0 0 0 rg 0 0 0 RG 0.3 w ");
        foreach (var y in new[] { 740, 700, 650, 600 })
            content.Append(filledRules ? $"40 {y} 530 0.3 re f " : $"40 {y} m 570 {y} l S ");
        foreach (var x in new[] { 40, 150, 280, 570 })
            content.Append(filledRules ? $"{x} 600 0.3 140 re f " : $"{x} 600 m {x} 740 l S ");
        content.Append("BT /F1 10 Tf 1 0 0 1 45 720 Tm (FIELD) Tj 1 0 0 1 155 720 Tm (PROGRAMMING) Tj " +
            "1 0 0 1 285 720 Tm (DESCRIPTION) Tj " +
            "1 0 0 1 45 680 Tm (RAMP0_INC) Tj 1 0 0 1 155 680 Tm (1677722) Tj " +
            "1 0 0 1 285 680 Tm (Positive ramp) Tj 1 0 0 1 285 666 Tm (Two's complement = 2) Tj " +
            "/F1 6 Tf 4 Ts (30) Tj 0 Ts /F1 10 Tf " +
            "1 0 0 1 45 630 Tm (RAMP1_INC) Tj 1 0 0 1 155 630 Tm (1072064102) Tj " +
            "1 0 0 1 285 630 Tm (Negative ramp) Tj 1 0 0 1 285 616 Tm (Recalibrate each cycle) Tj ET");
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 3 0 R >> /XObject << /Table 6 0 R >> >> /Contents 5 0 R >>",
            Stream(form ? "q 1 0 0 1 5 10 cm /Table Do Q" : content.ToString())
        };
        objects.Add($"<< /Type /XObject /Subtype /Form /BBox [0 0 600 800] /Matrix [0.9 0 0 0.9 20 30] /Resources << /Font << /F1 3 0 R >> >> /Length {Encoding.ASCII.GetByteCount(content.ToString())} >>\nstream\n{content}\nendstream");
        using var file = File.Create(path);
        void WriteAscii(string text) => file.Write(Encoding.ASCII.GetBytes(text));
        WriteAscii("%PDF-1.7\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(file.Position);
            WriteAscii($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = file.Position;
        WriteAscii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) WriteAscii(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        WriteAscii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return path;
    }

    public static string WriteDiagramLabels(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "PDF diagram labels.pdf");
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 306 505] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>",
            // Emit complete rows, reproducing PDFium's interleaved label order.
            Stream("BT /F1 10 Tf 166 423 Td (Warm) Tj 40 0 Td (Cold) Tj 34 0 Td (Warm) Tj " +
                "-67 -12 Td (air) Tj 38 0 Td (air) Tj 37 0 Td (air) Tj ET")
        };
        using var file = File.Create(path);
        void WriteAscii(string text) => file.Write(Encoding.ASCII.GetBytes(text));
        WriteAscii("%PDF-1.7\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(file.Position);
            WriteAscii($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = file.Position;
        WriteAscii($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) WriteAscii(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        WriteAscii($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return path;
    }

    public static string WriteFootnotes(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "PDF footnotes.pdf");
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>",
            // Deliberately emit the definitions first, as real datasheets do.
            Stream("BT /F1 10 Tf 40 130 Td (\\(2\\) Harmonics depend on board layout and components.) Tj 18 -12 Td (This continuation belongs to the same footnote.) Tj -18 -16 Td (\\(3\\) Lower VCO frequency can limit the detector.) Tj ET " +
                "BT /F1 14 Tf 40 650 Td (Second harmonic) Tj /F1 10 Tf 1 0 0 1 148 655 Tm (\\(2\\)) Tj ET " +
                "BT /F1 14 Tf 40 600 Td (Third harmonic) Tj /F1 10 Tf 1 0 0 1 134 605 Tm (\\(2\\)) Tj ET " +
                "BT /F1 14 Tf 40 550 Td (Normal value \\(2\\) is not a reference.) Tj ET " +
                "BT /F1 14 Tf 40 500 Td (Unknown note) Tj /F1 10 Tf 1 0 0 1 132 505 Tm (\\(9\\)) Tj ET")
        };
        using var file = File.Create(path);
        void WriteAscii(string text) => file.Write(Encoding.ASCII.GetBytes(text));
        WriteAscii("%PDF-1.7\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(file.Position);
            WriteAscii($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = file.Position;
        WriteAscii($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) WriteAscii(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        WriteAscii($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return path;
    }

    public static string Write(string directory, bool outline = true, bool offsetScannedOutline = false)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "PDF 阅读测试.pdf");
        var title = "FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("PDF 阅读测试"));
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {(outline ? "/Outlines 12 0 R /PageMode /UseOutlines" : "")} >>",
            "<< /Type /Pages /Kids [4 0 R 6 0 R 8 0 R 10 0 R] /Count 4 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>",
            Stream("0.18 0.40 0.30 rg 0 0 400 600 re f 1 1 1 rg BT /F1 32 Tf 45 460 Td (PDF READER) Tj 0 -60 Td /F1 18 Tf (First page cover) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Resources << /Font << /F1 3 0 R >> >> /Contents 7 0 R >>",
            Stream("1 1 1 rg 0 0 400 600 re f 0 0 0 rg BT /F1 22 Tf 40 530 Td (Chapter Two) Tj /F1 16 Tf 0 -65 Td (Select this text for a note.) Tj 0 -36 Td (Underline and highlight this line.) Tj 0 -36 Td (Search this text again.) Tj 0 -175 Td (Nested section) Tj 0 -40 Td (Notes stay on the correct page.) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Resources << /XObject << /Scan 17 0 R >> >> /Contents 9 0 R >>",
            Stream("q 400 0 0 600 0 0 cm /Scan Do Q"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 700] /CropBox [50 80 450 680] /Rotate 90 /Resources << /Font << /F1 3 0 R >> >> /Contents 11 0 R >>",
            Stream("0 0 0 rg BT /F1 24 Tf 90 580 Td (Rotated and cropped) Tj 0 -60 Td (Text geometry stays aligned.) Tj ET"),
            "<< /Type /Outlines /First 13 0 R /Last 16 0 R /Count 4 >>",
            "<< /Title (Cover) /Parent 12 0 R /Dest [4 0 R /Fit] /Next 14 0 R >>",
            "<< /Title (Chapter Two) /Parent 12 0 R /Dest [6 0 R /XYZ null 600 null] /Prev 13 0 R /Next 16 0 R /First 15 0 R /Last 15 0 R /Count 1 >>",
            "<< /Title (Nested section) /Parent 14 0 R /A << /S /GoTo /D [6 0 R /XYZ null 240 null] >> >>",
            $"<< /Title (Scanned page) /Parent 12 0 R /Dest [8 0 R {(offsetScannedOutline ? "/XYZ null 500 null" : "/Fit")}] /Prev 14 0 R >>",
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length 26 >>\nstream\nEEDDCC99BBAA557788CC9966>\nendstream",
            $"<< /Title <{title}> /Author (Kkindle Tests) >>"
        };
        using var file = File.Create(path);
        void WriteAscii(string text) => file.Write(Encoding.ASCII.GetBytes(text));
        WriteAscii("%PDF-1.7\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(file.Position);
            WriteAscii($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = file.Position;
        WriteAscii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) WriteAscii(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        WriteAscii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 18 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return path;
    }

    /// <summary>
    /// Two outline entries that share one destination: a datasheet puts several
    /// headings on the same page, so picking the second one must not fall back
    /// to the first entry that also covers that page.
    /// </summary>
    public static string WriteTiedOutline(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "PDF tied outline.pdf");
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 8 0 R /PageMode /UseOutlines >>",
            "<< /Type /Pages /Kids [4 0 R 6 0 R] /Count 2 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>",
            Stream("1 1 1 rg 0 0 400 600 re f 0 0 0 rg BT /F1 20 Tf 40 520 Td (Section one) Tj /F1 14 Tf 0 -60 Td (First entry body.) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Resources << /Font << /F1 3 0 R >> >> /Contents 7 0 R >>",
            Stream("1 1 1 rg 0 0 400 600 re f 0 0 0 rg BT /F1 20 Tf 40 520 Td (Section two) Tj ET"),
            "<< /Type /Outlines /First 9 0 R /Last 10 0 R /Count 2 >>",
            "<< /Title (First entry) /Parent 8 0 R /Dest [4 0 R /XYZ null 600 null] /Next 10 0 R >>",
            "<< /Title (Second entry) /Parent 8 0 R /Dest [4 0 R /XYZ null 600 null] /Prev 9 0 R >>"
        };
        using var file = File.Create(path);
        void WriteAscii(string text) => file.Write(Encoding.ASCII.GetBytes(text));
        WriteAscii("%PDF-1.7\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(file.Position);
            WriteAscii($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = file.Position;
        WriteAscii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) WriteAscii(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        WriteAscii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return path;
    }

    public static string WriteNavigation(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "PDF navigation and columns.pdf");
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [0 << /S /r >> 2 << /S /D /St 1 >>] >> >>",
            "<< /Type /Pages /Kids [4 0 R 6 0 R 8 0 R 10 0 R] /Count 4 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R /Annots [12 0 R 13 0 R 14 0 R 15 0 R] >>",
            Stream("0 0 0 rg BT /F1 14 Tf 1 0 0 1 40 700 Tm (Left first line.) Tj 1 0 0 1 340 700 Tm (Right first line.) Tj 1 0 0 1 40 675 Tm (An inter-) Tj 1 0 0 1 340 675 Tm (Right second line.) Tj 1 0 0 1 40 650 Tm (esting example.) Tj 1 0 0 1 340 650 Tm (Right third line.) Tj 1 0 0 1 40 100 Tm (Go to note) Tj 1 0 0 1 200 100 Tm (Website) Tj 1 0 0 1 340 100 Tm (Rotated page) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 3 0 R >> >> /Contents 7 0 R /Annots [16 0 R] >>",
            Stream("BT /F1 20 Tf 40 700 Td (Contents - return to columns) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 3 0 R >> >> /Contents 9 0 R /Annots [17 0 R] >>",
            Stream("BT /F1 20 Tf 40 750 Td (Body page one) Tj 0 -350 Td (Footnote destination) Tj 0 -60 Td (Return to reference) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 700] /CropBox [50 80 450 680] /Rotate 90 /Resources << /Font << /F1 3 0 R >> >> /Contents 11 0 R /Annots [18 0 R] >>",
            Stream("BT /F1 20 Tf 90 580 Td (Rotated return link) Tj ET"),
            "<< /Type /Annot /Subtype /Link /Rect [40 90 160 115] /Border [0 0 0] /Dest [8 0 R /XYZ 40 400 null] >>",
            "<< /Type /Annot /Subtype /Link /Rect [200 90 290 115] /A << /S /URI /URI (https://example.org/) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [40 40 100 60] /A << /S /JavaScript /JS (app.alert) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [340 90 490 115] /Dest [10 0 R /XYZ 90 580 null] >>",
            "<< /Type /Annot /Subtype /Link /Rect [40 690 360 720] /A << /S /GoTo /D [4 0 R /XYZ null 700 null] >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [40 330 260 360] /Dest [4 0 R /XYZ null 700 null] >>",
            "<< /Type /Annot /Subtype /Link /Rect [90 560 300 605] /Dest [4 0 R /Fit] >>"
        };
        using var file = File.Create(path);
        void WriteAscii(string text) => file.Write(Encoding.ASCII.GetBytes(text));
        WriteAscii("%PDF-1.7\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(file.Position);
            WriteAscii($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = file.Position;
        WriteAscii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) WriteAscii(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        WriteAscii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return path;
    }

    public static string WriteLong(string directory, int pageCount = 400)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "PDF continuous reading.pdf");
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(page => $"{4 + page * 2} 0 R"));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        for (var page = 1; page <= pageCount; page++)
        {
            var contents = 5 + (page - 1) * 2;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Resources << /Font << /F1 3 0 R >> >> /Contents {contents} 0 R >>");
            var text = new StringBuilder($"0 0 0 rg BT /F1 21 Tf 36 550 Td (Reading a PDF) Tj /F1 10 Tf 0 -26 Td (Page {page}) Tj 0 -23 Td /F1 10 Tf ");
            for (var line = 1; line <= 38; line++)
                text.Append($"(Line {line}: Text stays sharp as the page is enlarged.) Tj 0 -12 Td ");
            text.Append("ET");
            objects.Add(Stream(text.ToString()));
        }
        using var file = File.Create(path);
        void WriteAscii(string text) => file.Write(Encoding.ASCII.GetBytes(text));
        WriteAscii("%PDF-1.7\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(file.Position);
            WriteAscii($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = file.Position;
        WriteAscii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) WriteAscii(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        WriteAscii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return path;
    }

    private static string Stream(string content) => $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream";
}

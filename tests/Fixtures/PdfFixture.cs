using System.Globalization;
using System.Text;

namespace Kkindle.TestFixtures;

/// <summary>A deterministic PDF with text, a scan, nested outlines and a rotated crop.</summary>
internal static class PdfFixture
{
    public static string Write(string directory, bool outline = true)
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
            Stream("0 0 0 rg BT /F1 22 Tf 40 530 Td (Chapter Two) Tj /F1 16 Tf 0 -65 Td (Select this text for a note.) Tj 0 -36 Td (Underline and highlight this line.) Tj 0 -36 Td (Search this text again.) Tj 0 -175 Td (Nested section) Tj 0 -40 Td (Notes stay on the correct page.) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Resources << /XObject << /Scan 17 0 R >> >> /Contents 9 0 R >>",
            Stream("q 400 0 0 600 0 0 cm /Scan Do Q"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 700] /CropBox [50 80 450 680] /Rotate 90 /Resources << /Font << /F1 3 0 R >> >> /Contents 11 0 R >>",
            Stream("0 0 0 rg BT /F1 24 Tf 90 580 Td (Rotated and cropped) Tj 0 -60 Td (Text geometry stays aligned.) Tj ET"),
            "<< /Type /Outlines /First 13 0 R /Last 16 0 R /Count 4 >>",
            "<< /Title (Cover) /Parent 12 0 R /Dest [4 0 R /Fit] /Next 14 0 R >>",
            "<< /Title (Chapter Two) /Parent 12 0 R /Dest [6 0 R /XYZ null 600 null] /Prev 13 0 R /Next 16 0 R /First 15 0 R /Last 15 0 R /Count 1 >>",
            "<< /Title (Nested section) /Parent 14 0 R /A << /S /GoTo /D [6 0 R /XYZ null 240 null] >> >>",
            "<< /Title (Scanned page) /Parent 12 0 R /Dest [8 0 R /Fit] /Prev 14 0 R >>",
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

    private static string Stream(string content) => $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream";
}

using System.IO;

namespace Kkindle;

/// <summary>
/// Temporary diagnostic switch for the PDF annotation interactions. Enabled
/// only when KKINDLE_PDF_TRACE=1 so shipped builds stay quiet.
/// </summary>
internal static class PdfAnnotTrace
{
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("KKINDLE_PDF_TRACE") == "1";

    private static readonly object Gate = new();
    private static string? _path;

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                _path ??= Path.Combine(AppContext.BaseDirectory, "data", "logs", "pdf-annot-trace.log");
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never break the reader.
        }
    }
}

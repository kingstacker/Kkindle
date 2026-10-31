using Kkindle.Core;

namespace Kkindle.ApplicationServices;

/// <summary>Explicit adapter for platforms without an external converter.</summary>
public sealed class UnavailableBookFormatConverter : IBookFormatConverter
{
    public Task ConvertAsync(string sourcePath, string destinationPath,
        IProgress<FormatConversionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        FormatConversionMetadata? metadata = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new NotSupportedException("当前平台不支持书籍格式转换。"));
    }
}

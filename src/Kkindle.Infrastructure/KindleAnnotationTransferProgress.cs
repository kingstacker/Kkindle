using Kkindle.Core;

namespace Kkindle.Infrastructure;

internal sealed class KindleAnnotationTransferProgress(
    IProgress<KindleAnnotationSyncProgress> progress,
    KindleAnnotationSyncStage stage,
    string? bookTitle = null) : IProgress<TransferProgress>
{
    public void Report(TransferProgress value) =>
        progress.Report(new(stage, value.BytesCopied, value.TotalBytes, bookTitle));
}

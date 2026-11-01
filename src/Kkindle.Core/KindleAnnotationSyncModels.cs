namespace Kkindle.Core;

public enum KindleAnnotationSyncStage
{
    ReadingDevice, MatchingBooks, ProcessingBooks, ReadingBook,
    ImportingToComputer, PreparingWrite, BackingUp, WritingToDevice,
    Verifying, Ready, Completed
}

public sealed record KindleAnnotationSyncProgress(
    KindleAnnotationSyncStage Stage,
    long Completed = 0,
    long Total = 0,
    string? BookTitle = null)
{
    public double? Percentage => Total > 0
        ? Math.Clamp(100d * Completed / Total, 0, 100) : null;
}

public sealed record KindleAnnotationBookIdentity(
    string BookId,
    string? Asin,
    string? Guid);

public sealed record KindleAnnotationRecord(
    string AnnotationId,
    KindleAnnotationBookIdentity Book,
    int Dataset,
    KindleClippingType Type,
    long Start,
    long End,
    long CreatedTime,
    long ModifiedTime,
    string NoteText,
    string SerializedPayload,
    string BookTitle = "")
{
    public DateTimeOffset CreatedAt => DateTimeOffset.FromUnixTimeMilliseconds(CreatedTime);
    public DateTimeOffset UpdatedAt => DateTimeOffset.FromUnixTimeMilliseconds(ModifiedTime);
}

public sealed record KindleAnnotationSnapshot(
    string DatabaseRelativePath,
    string Sha256,
    IReadOnlyList<KindleAnnotationRecord> Records);

public sealed record KindleAnnotationMutation(
    string AnnotationId,
    string BookId,
    int Dataset,
    long Start,
    long End,
    long CreatedTime,
    long ModifiedTime,
    string NoteText,
    string SerializedPayload);

public sealed record KindleAnnotationWriteResult(
    int UpsertedRows,
    string PreviousSha256,
    string NewSha256,
    string? LocalBackupPath,
    string? DeviceBackupPath);

public sealed record KindleAnnotationSyncBookSummary(
    Guid LocalBookId,
    string LocalTitle,
    string DeviceTitle,
    string DeviceRelativePath,
    int LocalCount,
    int DeviceCount,
    int ChangesToComputer,
    int ChangesToDevice,
    int UnsupportedCount);

public sealed record KindleAnnotationSyncPreview(
    bool HasChanges,
    string Signature,
    IReadOnlyList<KindleAnnotationSyncBookSummary> Books)
{
    public int DifferenceCount => Books.Sum(book => book.ChangesToComputer + book.ChangesToDevice);
    public int BookCount => Books.Count;
    public int UnsupportedCount => Books.Sum(book => book.UnsupportedCount);
}

public sealed record KindleAnnotationSyncResult(
    int BooksSynchronized,
    int ChangesToComputer,
    int ChangesToDevice,
    int UnsupportedCount,
    string? LocalBackupPath,
    string? DeviceBackupPath);

internal sealed record KindleAnnotationSyncPreparedEntry(
    KindleAnnotationRecord Record,
    string SelectedText,
    string? ParentHighlightId);

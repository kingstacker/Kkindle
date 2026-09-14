namespace Kkindle.Core;

public enum LocalDataChangeKind
{
    Library,
    Annotation,
    Bookmark,
    ReadingProgress,
    ReadingLayout,
    ReadingStats,
    Settings,
    ReadingDataReset
}

public sealed class LocalDataChangedEventArgs(LocalDataChangeKind kind) : EventArgs
{
    public LocalDataChangeKind Kind { get; } = kind;
}

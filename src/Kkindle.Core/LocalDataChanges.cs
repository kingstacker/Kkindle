namespace Kkindle.Core;

public enum LocalDataChangeKind
{
    Library,
    Annotation,
    Bookmark,
    ReadingProgress,
    ReadingLayout,
    ReadingStats,
    Settings
}

public sealed class LocalDataChangedEventArgs(LocalDataChangeKind kind) : EventArgs
{
    public LocalDataChangeKind Kind { get; } = kind;
}

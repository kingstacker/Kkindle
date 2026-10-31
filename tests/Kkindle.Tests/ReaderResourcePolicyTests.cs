using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class ReaderResourcePolicyTests
{
    [Fact]
    public void RestorationFindsTheChapterAndNormalizesTheSavedLocation()
    {
        var document = Document();
        var progress = Progress("chapters/second.xhtml") with
        {
            ChapterIndex = 99, ScrollPosition = -42, Fragment = "#part%202",
            ContentPosition = new ReaderContentPosition { SliceFraction = double.NaN }
        };
        var restored = ReaderResourcePolicy.RestoreProgress(document, progress);
        Assert.NotNull(restored);
        Assert.Equal(1, restored.ChapterIndex);
        Assert.Equal(0, restored.ScrollPosition);
        Assert.Equal("part 2", restored.Fragment);
        Assert.Null(restored.ContentPosition);
        Assert.Equal(99, progress.ChapterIndex);
    }

    [Theory]
    [InlineData("../outside.xhtml")]
    [InlineData("chapters/missing.xhtml")]
    [InlineData("")]
    public void RestorationRejectsInvalidOrMissingChapters(string chapter)
        => Assert.Null(ReaderResourcePolicy.RestoreProgress(Document(), Progress(chapter)));

    [Fact]
    public void RestorationPreservesAValidContentBreakpoint()
    {
        var position = new ReaderContentPosition { TextOffset = 500, PageIndex = 3, SliceFraction = 0.5 };
        var progress = Progress("first.xhtml") with { ContentPosition = position, ScrollPosition = 200 };
        var restored = ReaderResourcePolicy.RestoreProgress(Document(), progress);
        Assert.NotNull(restored);
        Assert.Same(position, restored.ContentPosition);
        Assert.Equal(200, restored.ScrollPosition);
        Assert.Equal(progress.BookFileId, restored.BookFileId);
    }

    [Fact]
    public void PathBoundaryDoesNotTreatASiblingPrefixAsContained()
    {
        var root = Document().RootPath;
        Assert.True(ReaderResourcePolicy.IsPathInside(root, Path.Combine(root, "chapter.xhtml")));
        Assert.False(ReaderResourcePolicy.IsPathInside(root, Path.Combine(root + "-other", "chapter.xhtml")));
        Assert.Null(ReaderResourcePolicy.RestoreProgress(Document(), null));
    }

    [Fact]
    public void PathAndChapterMatchingFollowPlatformCaseSensitivity()
    {
        var document = Document();
        var caseChangedRoot = document.RootPath.ToUpperInvariant();
        var caseChangedPath = Path.Combine(caseChangedRoot, "FIRST.XHTML");
        Assert.Equal(OperatingSystem.IsWindows(), ReaderResourcePolicy.IsPathInside(document.RootPath, caseChangedPath));
        Assert.Equal(OperatingSystem.IsWindows(), ReaderResourcePolicy.RestoreProgress(document, Progress("FIRST.XHTML")) is not null);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("#", null)]
    [InlineData("#part%202", "part 2")]
    [InlineData("#bad%fragment", "bad%fragment")]
    public void FragmentDecodingRetainsExistingReaderSemantics(string? value, string? expected)
        => Assert.Equal(expected, ReaderResourcePolicy.DecodeFragment(value));

    private static EpubReaderDocument Document()
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-progress-policy");
        return new(root, [Path.Combine(root, "first.xhtml"), Path.Combine(root, "chapters", "second.xhtml")], [], []);
    }

    private static ReaderProgressRow Progress(string chapter)
        => new(Guid.NewGuid(), Guid.NewGuid(), chapter, null, 0, 0, 0, 0, DateTimeOffset.UtcNow);
}

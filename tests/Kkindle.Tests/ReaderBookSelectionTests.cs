using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class ReaderBookSelectionTests
{
    [Fact]
    public void PrefersEpubRegardlessOfLibraryFileOrder()
    {
        var pdf = new BookFile { Format = "pdf" };
        var epub = new BookFile { Format = "EPUB" };

        Assert.Same(epub, ReaderBookSelectionPolicy.SelectPreferred([pdf, epub]));
    }

    [Fact]
    public void FallsBackToPdfWhenEpubIsMissing()
    {
        var mobi = new BookFile { Format = "mobi" };
        var pdf = new BookFile { Format = "pdf" };

        Assert.Same(pdf, ReaderBookSelectionPolicy.SelectPreferred([mobi, pdf]));
    }

    [Fact]
    public void FallsBackToAzw3WhenOnlyAzw3IsAvailable()
    {
        var azw3 = new BookFile { Format = "azw3" };
        Assert.Same(azw3, ReaderBookSelectionPolicy.SelectPreferred([
            new BookFile { Format = "mobi" },
            azw3
        ]));
    }

    [Fact]
    public void ListsAllSupportedFormatsInReaderPreferenceOrder()
    {
        var pdf = new BookFile { Format = "pdf" };
        var mobi = new BookFile { Format = "mobi" };
        var azw3 = new BookFile { Format = "azw3" };
        var epub = new BookFile { Format = "EPUB" };

        Assert.Equal([epub, pdf, azw3, mobi], ReaderBookSelectionPolicy.GetSupportedFiles([
            pdf, mobi, azw3, epub
        ]));
    }

    [Fact]
    public void ReturnsNullWhenNoReaderFormatExists()
    {
        Assert.Equal("mobi", ReaderBookSelectionPolicy.SelectPreferred([new BookFile { Format = "mobi" }])?.Format);
        Assert.Null(ReaderBookSelectionPolicy.SelectPreferred(null));
    }

    [Fact]
    public void HonorsConfiguredPreferredFormat()
    {
        var epub = new BookFile { Format = "epub" };
        var mobi = new BookFile { Format = "mobi" };

        Assert.Same(mobi, ReaderBookSelectionPolicy.SelectPreferred([epub, mobi], ".MOBI"));
        Assert.Same(epub, ReaderBookSelectionPolicy.SelectPreferred([epub, mobi], "unknown"));
    }

    [Fact]
    public void PrefersGeneratedPinyinEditionWhenOpeningTheBook()
    {
        var original = new BookFile
        {
            Format = "epub",
            RelativePath = "book.epub"
        };
        var pinyin = new BookFile
        {
            Format = "epub",
            RelativePath = "book-拼音版.epub"
        };

        Assert.Same(pinyin, ReaderBookSelectionPolicy.SelectPreferred([original, pinyin]));
        Assert.Same(pinyin, ReaderBookSelectionPolicy.SelectPreferred([original, pinyin], "epub"));
    }

    [Fact]
    public void SelectEpubOnlyReturnsEpubForAnnotationMaterials()
    {
        var pdf = new BookFile { Format = "pdf" };
        var epub = new BookFile { Format = " EPUB " };

        Assert.Same(epub, ReaderBookSelectionPolicy.SelectEpub([pdf, epub]));
        Assert.Null(ReaderBookSelectionPolicy.SelectEpub([pdf]));
        Assert.Null(ReaderBookSelectionPolicy.SelectEpub(null));
    }
}

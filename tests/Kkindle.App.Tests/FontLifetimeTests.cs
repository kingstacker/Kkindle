using Avalonia;
using Avalonia.Media;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class FontLifetimeTests(SettingsUiSession session)
{
    [Theory]
    [InlineData(FontStyle.Normal, FontWeight.Normal, FontSimulations.None)]
    [InlineData(FontStyle.Italic, FontWeight.Normal, FontSimulations.Oblique)]
    [InlineData(FontStyle.Normal, FontWeight.SemiBold, FontSimulations.Bold)]
    [InlineData(FontStyle.Normal, FontWeight.Bold, FontSimulations.Bold)]
    [InlineData(FontStyle.Italic, FontWeight.Bold, FontSimulations.Bold | FontSimulations.Oblique)]
    [InlineData(FontStyle.Italic, FontWeight.Black, FontSimulations.Bold | FontSimulations.Oblique)]
    public Task BundledReaderFontKeepsTheRequestedStyle(FontStyle style, FontWeight weight, FontSimulations expected) =>
        session.Session.Dispatch(() =>
        {
            // The bundled face is the default for both the application chrome
            // and the reader fallback.
            var family = new FontFamily("fonts:Kkindle#KingHwaOldSong");
            Assert.Equal("fonts:Kkindle", family.Key?.Source.ToString());
            Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(family, style, weight), out var glyphs));
            Assert.NotNull(glyphs);
            Assert.Equal("KingHwaOldSong", glyphs.FamilyName);
            Assert.Equal(expected, glyphs.FontSimulations);
            Assert.Equal(expected, glyphs.PlatformTypeface.FontSimulations);
            Assert.True(glyphs.GlyphCount > 0);
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task ApplicationChromeUsesFixedUiFont() => session.Session.Dispatch(() =>
    {
        var family = Assert.IsType<FontFamily>(Application.Current!.Resources["DefaultAppFontFamily"]);
        Assert.Contains("KingHwaOldSong", family.Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft YaHei UI", family.Name, StringComparison.OrdinalIgnoreCase);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task BundledFontCanCreateSyntheticFacesDuringGarbageCollection() => session.Session.Dispatch(async () =>
    {
        using var cancellation = new CancellationTokenSource();
        var collector = Task.Run(() =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(1);
            }
        });
        try
        {
            for (var index = 0; index < 96; index++)
            {
                // A fresh collection forces actual synthesis; requests for
                // nearby weights in the global manager reuse a cached face.
                using var collection = new BundledAppFontCollection();
                Assert.True(collection.TryGetGlyphTypeface("KingHwaOldSong", FontStyle.Normal,
                    FontWeight.Normal, FontStretch.Normal, out var original));
                Assert.True(collection.TryCreateSyntheticGlyphTypeface(original!, FontStyle.Italic,
                    FontWeight.Bold, FontStretch.Normal, out var glyphs));
                Assert.NotNull(glyphs);
                Assert.True(glyphs.GlyphCount > 0);
                Assert.True(glyphs.FontSimulations.HasFlag(FontSimulations.Bold | FontSimulations.Oblique));
            }
        }
        finally
        {
            cancellation.Cancel();
            await collector;
        }
        return true;
    }, CancellationToken.None);
}

using System.IO.Compression;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Kkindle.Core;
using Kkindle.Infrastructure;
using SkiaSharp;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class ReaderHighlightColorTests(SettingsUiSession session)
{
    internal static readonly string[] MarkerColors = ["#FFD54F", "#81C784", "#64B5F6", "#F48FB1", "#B39DDB", "#FFB74D"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ColoredMarkersKeepThemeTextWhileBlackMarkersInvertIt(bool vertical) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope, new(VerticalWriting: vertical));
        var start = host.BodyText!.IndexOf("第1段", StringComparison.Ordinal);
        Assert.True(start >= 0);
        foreach (var theme in new[] { ReaderTheme.Classic, ReaderTheme.Night })
        {
            host.SetAppearance(new ReaderAppearanceSettings { Theme = theme });
            host.SetAnnotations([]);
            using var baseline = SKBitmap.Decode(await host.CaptureVisiblePageAsync(CancellationToken.None));
            Assert.NotNull(baseline);
            var palette = ReaderPalette.For(theme);
            var ink = ReaderPalette.ToSkia(palette.Ink);
            foreach (var color in MarkerColors.Append("#000000"))
            {
                host.SetAnnotations([new ReaderAnnotation
                {
                    StartOffset = start, EndOffset = start + 18,
                    UnderlineStyle = "marker", Color = color
                }]);
                using var marked = SKBitmap.Decode(await host.CaptureVisiblePageAsync(CancellationToken.None));
                Assert.NotNull(marked);
                var changedInk = 0;
                var coloredBackground = 0;
                var expected = BlendMarker(color, palette.Page);
                for (var y = 0; y < baseline.Height; y++)
                    for (var x = 0; x < baseline.Width; x++)
                    {
                        var before = baseline.GetPixel(x, y);
                        var after = marked.GetPixel(x, y);
                        if (before == ink && ColorDistance(after, ink) > 2) changedInk++;
                        if (ColorDistance(after, expected) <= 2) coloredBackground++;
                    }
                if (color == "#000000")
                    Assert.True(changedInk > 10, $"Black marker must invert text in {theme}, vertical={vertical}.");
                else
                {
                    Assert.Equal(0, changedInk);
                    Assert.True(coloredBackground > 100, $"Missing {color} marker in {theme}, vertical={vertical}.");
                }
            }
        }
        scope.Set("_readerActiveHost", null);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
    });

    [Theory]
    [InlineData("zh-CN", ReaderTheme.Classic)]
    [InlineData("en-US", ReaderTheme.Classic)]
    [InlineData("zh-CN", ReaderTheme.Night)]
    public Task ColorMenuPreservesExistingNotesAndRemembersTheNewMarkerColor(string language, ReaderTheme theme) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope, new());
        ((Kkindle.App)Application.Current!).ApplyLanguage(language);
        scope.Call("ChangeReaderAppearance", new ReaderAppearanceSettings { Theme = theme });
        await scope.Field<Task>("_readerAppearanceSaveTask");
        var path = Path.Combine(scope.Paths.ReaderCache, "highlights.epub");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry("mimetype", "application/epub+zip");
            WriteEntry("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='content.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            WriteEntry("content.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='2.0' unique-identifier='book'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='book'>highlight-colors</dc:identifier><dc:title>Highlight colors</dc:title></metadata><manifest><item id='chapter' href='appearance.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='chapter'/></spine></package>");
            WriteEntry("appearance.xhtml", await File.ReadAllTextAsync(host.Source!.LocalPath));

            void WriteEntry(string name, string contents)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(contents);
            }
        }
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.ImportAsync([path]);
        var book = Assert.Single(await library.SearchAsync());
        var file = Assert.Single(book.Files);
        using var card = new BookCardViewModel(book, scope.Paths.Data);
        scope.Set("_readerBookCard", card);
        scope.Set("_readerBookFile", file);
        var text = host.BodyText!;
        var start = text.IndexOf("第1段", StringComparison.Ordinal);
        Select(start);
        await scope.Call<Task>("SaveReaderAnnotationAsync", "Keep this note", "marker", MarkerColors[0]);
        var annotationId = Assert.Single(scope.Window.ReaderAnnotations).Id;

        var button = scope.Get<Button>("ReaderSelectionHighlightMenuButton");
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        Assert.Equal(6, flyout.Items.OfType<MenuItem>().Count());
        Assert.Single(flyout.Items.OfType<MenuItem>(), item => item.Tag as string == "marker");
        var picker = scope.Get<Button>("ReaderSelectionMarkerColorButton");
        var palette = Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(picker));
        var items = scope.Get<StackPanel>("ReaderSelectionMarkerPalette").Children.OfType<Button>().ToArray();
        Assert.Equal(7, items.Length);
        foreach (var item in items)
        {
            var before = Assert.Single(scope.Window.ReaderAnnotations).Color;
            await ShowStyles(start); // Re-selecting the same range edits the existing note.
            Assert.Equal(language == "zh-CN" ? "荧光标记" : "Highlight", scope.Get<TextBlock>("ReaderSelectionMarkerLabel").Text);
            Assert.Equal(Color.Parse(before), Assert.IsAssignableFrom<ISolidColorBrush>(scope.Get<Border>("ReaderSelectionMarkerColorPreview").Background).Color);
            if (item == items[^1]) CaptureMenu(TopLevel.GetTopLevel(picker), language + "-" + theme + "-row");
            Click(picker);
            await ReaderTests.Render();
            Assert.True(flyout.IsOpen);
            Assert.True(palette.IsOpen);
            Assert.True(scope.Get<Popup>("ReaderSelectionHostPopup").IsOpen);
            Assert.False(string.IsNullOrWhiteSpace(scope.Field<string?>("_readerPendingSelection")));
            Assert.Equal(before, Assert.Single(scope.Window.ReaderAnnotations).Color);
            scope.Call("StartReaderSelectionHighlightPointerTracking");
            await ReaderTests.Render();
            Assert.True(flyout.IsOpen); // Moving into the palette must not dismiss its parent.
            var color = Assert.IsType<string>(item.Tag);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(item)));
            var swatch = Assert.IsType<Border>(item.Content);
            Assert.Equal(Color.Parse(color), Assert.IsAssignableFrom<ISolidColorBrush>(swatch.Background).Color);
            if (item == items[^1]) CaptureMenu(TopLevel.GetTopLevel(item), language + "-" + theme + "-palette");
            Click(item);
            await WaitForSave();
            var saved = Assert.Single(scope.Window.ReaderAnnotations);
            Assert.Equal(annotationId, saved.Id);
            Assert.Equal(color, saved.Color);
            Assert.Equal("Keep this note", saved.Note);
            Assert.False(palette.IsOpen);
            Assert.False(flyout.IsOpen);
        }

        var lastColor = Assert.IsType<string>(items[^1].Tag);
        await ShowStyles(start + 30);
        Click(scope.Get<TextBlock>("ReaderSelectionMarkerLabel"));
        await WaitForSave();
        var second = scope.Window.ReaderAnnotations.Single(item => item.Id != annotationId);
        Assert.Equal("marker", second.UnderlineStyle);
        Assert.Equal(lastColor, second.Color);

        Select(start);
        scope.Set("_selectedReaderAnnotation", scope.Window.ReaderAnnotations.Single(item => item.Id == annotationId));
        await scope.Call<Task>("SaveReaderAnnotationAsync", "Edited note", null, null);
        var reopened = new ReaderDataService(scope.Paths);
        await reopened.InitializeAsync();
        var notes = await reopened.GetAnnotationsAsync(file.Id);
        Assert.Equal(2, notes.Count);
        Assert.All(notes, note => Assert.Equal(lastColor, note.Color));
        Assert.Equal("Edited note", notes.Single(note => note.Id == annotationId).Note);

        Select(start);
        await scope.Call<Task>("SaveReaderAnnotationAsync", "", null, null);
        Assert.Empty(scope.Window.ReaderAnnotations.Single(item => item.Id == annotationId).Note);
        scope.Set("_readerActiveHost", null);
        scope.Set("_readerBookCard", null);
        scope.Set("_readerBookFile", null);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;

        async Task ShowStyles(int offset)
        {
            Select(offset);
            scope.Call("ShowReaderSelectionPopup", new Point(450, 180), 210d);
            await ReaderTests.Render();
            flyout.ShowAt(button);
            await ReaderTests.Render();
        }

        async Task WaitForSave()
        {
            for (var attempt = 0; attempt < 500 && scope.Field<string?>("_readerPendingSelection") is not null; attempt++)
                await Task.Delay(10);
            Assert.Null(scope.Field<string?>("_readerPendingSelection"));
        }

        void Select(int offset)
        {
            scope.Set("_selectedReaderAnnotation", null);
            scope.Set("_readerPendingSelection", text.Substring(offset, 12));
            scope.Set("_readerPendingSelectionStartOffset", offset);
            scope.Set("_readerPendingSelectionEndOffset", offset + 12);
        }
    });

    internal static SKColor BlendMarker(string value, Color paper)
    {
        var color = Color.Parse(value);
        static byte Blend(byte foreground, byte background) => (byte)((foreground * 80 + background * 175 + 127) / 255);
        return new SKColor(Blend(color.R, paper.R), Blend(color.G, paper.G), Blend(color.B, paper.B));
    }

    internal static int ColorDistance(SKColor a, SKColor b) =>
        Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));

    internal static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point);
        root.MouseDown(point, MouseButton.Left);
        root.MouseUp(point, MouseButton.Left);
    }

    private static void CaptureMenu(Visual? visual, string language)
    {
        var directory = Environment.GetEnvironmentVariable("KKINDLE_READER_ARTIFACTS");
        if (visual is null || string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(visual.Bounds.Width), (int)Math.Ceiling(visual.Bounds.Height)));
        bitmap.Render(visual);
        bitmap.Save(Path.Combine(directory, language + "-highlight-colors.png"), PngBitmapEncoderOptions.Default);
    }

    private Task Run(Func<Task> action) => session.Session.Dispatch(async () => { await action(); return true; }, CancellationToken.None);
}

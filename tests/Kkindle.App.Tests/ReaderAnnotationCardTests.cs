using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
[Trait("Category", "Slow")]
public sealed class ReaderAnnotationCardTests(SettingsUiSession session)
{
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, true, false)]
    [InlineData(0, false, false)]
    [InlineData(1, false, true)]
    public Task EpubHoverUsesTheWholeQuoteAndOnlyDisplaysTheNote(int flow, bool spread, bool vertical) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope,
            new(FlowMode: flow, TwoPageMode: spread, VerticalWriting: vertical));
        var annotation = new ReaderAnnotation { StartOffset = 12, EndOffset = 110, Note = "只显示批注正文，不遮挡选文。" };
        scope.Window.ReaderAnnotations.Add(annotation);
        host.SetAnnotations([annotation]);
        var bands = host.GetAnnotationBounds(annotation);
        Assert.NotEmpty(bands);
        var first = bands[0];
        var last = bands[^1];
        var popup = scope.Get<Popup>("ReaderAnnotationHoverHostPopup");
        scope.Call("ShowReaderAnnotationHoverPopup", annotation.Note, first.Center, annotation, false);
        await ReaderTests.Render();
        var position = popup.PlacementRect!.Value.Position;
        scope.Call("ShowReaderAnnotationHoverPopup", annotation.Note, last.Center, annotation, false);
        await ReaderTests.Render();
        Assert.Equal(position, popup.PlacementRect!.Value.Position);
        var card = scope.Get<Border>("ReaderAnnotationHoverPopup");
        Assert.Equal(new Thickness(1), card.BorderThickness);
        var actualPosition = card.TranslatePoint(default, host)!.Value;
        Assert.InRange(Math.Abs(actualPosition.X - position.X), 0, 1);
        Assert.InRange(Math.Abs(actualPosition.Y - position.Y), 0, 1);
        Assert.False(scope.Get<Grid>("ReaderAnnotationHoverHeading").IsVisible);
        var bounds = new Rect(actualPosition, card.Bounds.Size);
        Assert.All(bands, band => Assert.False(bounds.Intersects(band), $"Card {bounds} overlaps quote {band}"));
        Assert.InRange(bounds.Left, 0, host.Bounds.Width - bounds.Width);
        Assert.InRange(bounds.Top, 0, host.Bounds.Height - bounds.Height);
        Capture(card, $"epub-note-only-{flow}-{spread}-{vertical}");
        // The actual host emits hover/leave from pointer movement.
        scope.Call("HideReaderAnnotationHoverPopup");
        var events = new List<string>();
        host.WebMessageReceived += (_, e) => { events.Add(e.Body!); scope.Call("HandleReaderBridgeMessage", e.Body); };
        var point = host.TranslatePoint(first.Center, scope.Window)!.Value;
        scope.Window.MouseMove(point);
        await ReaderTests.Render();
        Assert.True(popup.IsOpen, string.Join(" | ", events));
        scope.Window.MouseMove(host.TranslatePoint(new Point(1, 1), scope.Window)!.Value);
        await Task.Delay(220);
        await ReaderTests.Render();
        Assert.False(popup.IsOpen, string.Join(" | ", events));
        scope.Set("_readerActiveHost", null);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
    });

    [Fact]
    public Task EpubPreviewGrowsAndLongNotesRemainScrollable() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        await ReaderTests.Render();
        void Show(string note) => scope.Call("ShowReaderAnnotationHoverPopup", note, new Point(60, 60), null, false);
        Show("一条简短的批注");
        await ReaderTests.Render();
        var card = scope.Get<Border>("ReaderAnnotationHoverPopup");
        var shortHeight = card.Height;
        var note = string.Join('\n', Enumerable.Repeat("较长批注需要完整显示，随内容自动换行。", 7));
        Show(note);
        await ReaderTests.Render();
        Assert.Equal(new CornerRadius(0), card.CornerRadius);
        Assert.True(card.Height > shortHeight + 100);
        var scroll = card.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Capture(card, "epub-annotation-adaptive");
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1,
            $"Card {card.Height}; text {scroll.Extent}; viewport {scroll.Viewport}; reader {scope.Get<Control>("ReaderWebViewHost").Bounds}");

        Show(string.Join('\n', Enumerable.Repeat(note, 12)));
        await ReaderTests.Render();
        Assert.True(card.Height <= scope.Get<Control>("ReaderWebViewHost").Bounds.Height);
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        Assert.True(scope.Get<Popup>("ReaderAnnotationHoverHostPopup").IsHitTestVisible);
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        await ReaderTests.Render();
        Assert.True(scroll.Offset.Y > 0);
        scope.Call("HideReaderAnnotationHoverPopup");
    });

    [Fact]
    public Task PdfCardsUseContentHeightAndRewrapWhenTheGutterNarrows() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var pdf = new NativePdfReaderHost();
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = pdf;
        scope.Set("_readerActiveHost", pdf);
        scope.Set("_readerIsPdf", true);
        scope.Set("_readerPdfPage", 2);
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Window.Width = 1600;
        scope.Window.Height = 1000;
        await ReaderTests.Render();
        var path = PdfFixture.Write(scope.Paths.ReaderCache);
        Assert.True(await pdf.NavigateAsync(new Uri(new Uri(path).AbsoluteUri + "#page=2")));
        await pdf.SetZoomAsync(0.5);
        await ReaderTests.Render();
        ReaderAnnotation Note(double y, string text) => new()
        {
            ChapterPath = "pdf:page:2", Fragment = ReaderPdfPointAnchor.Encode(0.7, y), Note = text
        };
        var longer = Note(0.25, string.Join('\n', Enumerable.Repeat("批注按内容长度自动显示。", 7)));
        var shorter = Note(0.45, "短批注");
        scope.Window.ReaderAnnotations.Add(longer);
        scope.Window.ReaderAnnotations.Add(shorter);
        scope.Call("ShowCurrentReaderPdfAnnotation");
        await ReaderTests.Render();
        var primary = scope.Get<Border>("ReaderAnnotationHoverPopup");
        var secondary = (Border)Assert.Single(scope.Field<List<Popup>>("_readerPdfAnnotationPopups")).Child!;
        Assert.True(primary.IsVisible);
        Assert.True(secondary.IsVisible);
        Assert.Equal(new CornerRadius(0), primary.CornerRadius);
        Assert.Equal(new CornerRadius(0), secondary.CornerRadius);
        Capture(primary, "pdf-annotation-adaptive");
        Assert.True(primary.Height > secondary.Height + 80,
            $"Primary {primary.Bounds}; secondary {secondary.Bounds}");
        var scroll = primary.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1,
            $"Card {primary.Bounds}; text {scroll.Extent}; viewport {scroll.Viewport}");

        Assert.True(pdf.TryGetAnnotationAnchor(longer, out var page, out var anchor));
        var measure = ReaderAnnotationCardLayout.CreateMeasure(primary, scope.Get<TextBlock>("ReaderAnnotationHoverNote"));
        Assert.True(pdf.TryGetAnnotationCalloutBounds(page, anchor, new Size(220, 68), out var wide, measure));
        Assert.True(pdf.TryGetAnnotationCalloutBounds(page, anchor, new Size(150, 68), out var narrow, measure));
        Assert.True(narrow.Height > wide.Height);
        scope.Call("HideReaderAnnotationHoverPopup");
        scope.Set("_readerActiveHost", null);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
    });

    private static void Capture(Control card, string name)
    {
        var output = Environment.GetEnvironmentVariable("KKINDLE_READER_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(output) || TopLevel.GetTopLevel(card) is not { } root) return;
        Directory.CreateDirectory(output);
        using var frame = root.CaptureRenderedFrame();
        frame!.Save(Path.Combine(output, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private Task Run(Func<Task> action) => session.Session.Dispatch(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);
}

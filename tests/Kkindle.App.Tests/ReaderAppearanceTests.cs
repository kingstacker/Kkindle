using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Layout;
using SkiaSharp;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
[Trait("Category", "Slow")]
public sealed class ReaderAppearanceTests(SettingsUiSession session)
{
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, false, true)]
    [InlineData(0, false, false)]
    [InlineData(1, true, false)]
    public Task ChangingEveryThemeKeepsPaginationPositionAndSelection(int flow, bool spread, bool vertical) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await LoadChapter(scope, new(FlowMode: flow, TwoPageMode: spread, VerticalWriting: vertical));
        using var preload = new NativeReaderHost();
        scope.Set("_readerPreloadHost", preload);
        Assert.True(host.PageCount > 3);
        host.SeekToRatio(0.4);
        var layout = NativeField<ChapterLayout>(host, "_layout");
        var state = host.GetScrollState();
        var page = host.CurrentPage;
        var quote = host.GetCurrentPageQuote();
        var visiblePage = flow == 0 ? (int)(state.Position / state.ClientHeight) : page;
        var start = layout.Pages[visiblePage].TextStartOffset;
        NativeSet(host, "_selectionStart", start);
        NativeSet(host, "_selectionEnd", start + 6);
        host.SetSearchHighlights([(start + 10, 5), (start + 25, 6)], 0);
        // Focusing a search hit intentionally jumps to its page start. Restore
        // the fractional scroll offset before testing a paint-only change.
        host.SeekToRatio(0.4);

        foreach (var theme in Enum.GetValues<ReaderTheme>())
        {
            var appearance = new ReaderAppearanceSettings { Theme = theme, PaperEnabled = true, FibersEnabled = true };
            scope.Call("ChangeReaderAppearance", appearance);
            await scope.Field<Task>("_readerAppearanceSaveTask");
            await ReaderTests.Render();
            Assert.Same(layout, NativeField<ChapterLayout>(host, "_layout"));
            Assert.True(state == host.GetScrollState(), $"{theme}: expected {state}, got {host.GetScrollState()}");
            Assert.Equal(page, host.CurrentPage);
            Assert.Equal(quote, host.GetCurrentPageQuote());
            Assert.Equal(start, NativeField<int>(host, "_selectionStart"));
            Assert.Equal(start + 6, NativeField<int>(host, "_selectionEnd"));
            Assert.Equal(appearance, NativeField<ReaderAppearanceSettings>(preload, "_appearance"));
            using var painted = SKBitmap.Decode(await host.CaptureVisiblePageAsync(CancellationToken.None));
            Assert.NotNull(painted);
            var palette = ReaderPalette.For(theme);
            Assert.InRange(ColorDistance(painted.GetPixel(2, 2), ReaderPalette.ToSkia(palette.Page)), 0, 25);
            if (flow == 1 && !spread && !vertical) Capture(scope.Window, "theme-" + theme);
        }
        scope.Set("_readerPreloadHost", null);
        scope.Set("_readerActiveHost", null);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
    });

    [Fact]
    public Task SettingsControlsKeepClassicDefaultAndPersistPaperIndependently() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await LoadChapter(scope, new());
        Assert.Equal(ReaderTheme.Classic, scope.Field<AppSettings>("_appSettings").ReaderAppearance.Theme);
        Assert.False(scope.Get<CheckBox>("ReaderPaperEnabledCheck").IsChecked);
        Capture(scope.Window, "default-classic");
        scope.Call("ReaderLayoutSettingsButton_Click", null, new RoutedEventArgs());
        Assert.True(scope.Get<RadioButton>("ReaderClassicThemeOption").IsChecked);
        scope.Get<RadioButton>("ReaderGreenThemeOption").IsChecked = true;
        scope.Get<CheckBox>("ReaderPaperEnabledCheck").IsChecked = true;
        scope.Get<Slider>("ReaderPaperStrengthSlider").Value = 75;
        scope.Get<CheckBox>("ReaderFibersEnabledCheck").IsChecked = true;
        scope.Get<CheckBox>("ReaderPaperEnabledCheck").IsChecked = false;
        Assert.False(scope.Get<StackPanel>("ReaderPaperOptions").IsEnabled);
        // Closing the popup and immediately flushing also waits for the shared
        // settings gate, as application shutdown does.
        scope.Call("ReaderLayoutSettingsCloseButton_Click", null, new RoutedEventArgs());
        Assert.True(await scope.Call<Task<bool>>("FlushAppSettingsAsync"));
        var restored = (await new AppSettingsStore(scope.Paths).LoadAsync()).ReaderAppearance;
        Assert.Equal(ReaderTheme.Green, restored.Theme);
        Assert.False(restored.PaperEnabled);
        Assert.True(restored.FibersEnabled);
        Assert.Equal(0.75, restored.PaperStrength);
        using var nextBook = (NativeReaderHost)scope.Call<IReaderHost>("CreateReaderHostForCurrentFormat");
        Assert.Equal(restored, NativeField<ReaderAppearanceSettings>(nextBook, "_appearance"));
        scope.Call("ReaderLayoutSettingsButton_Click", null, new RoutedEventArgs());
        Assert.True(scope.Get<CheckBox>("ReaderFibersEnabledCheck").IsChecked);
        scope.Get<CheckBox>("ReaderPaperEnabledCheck").IsChecked = true;
        scope.Get<RadioButton>("ReaderNightThemeOption").IsChecked = true;
        await scope.Field<Task>("_readerAppearanceSaveTask");
        await ReaderTests.Render();
        Assert.True(scope.Get<TextBlock>("ReaderNightPaperHint").IsVisible);
        var card = scope.Get<Border>("ReaderLayoutSettingsCard");
        Assert.InRange(card.Bounds.Height, 200, scope.Window.Bounds.Height);
        CapturePopup(scope.Get<Popup>("ReaderLayoutSettingsPopup"), "settings-night");
        scope.Window.Height = 664;
        scope.Call("ReaderLayoutSettingsButton_Click", null, new RoutedEventArgs());
        await ReaderTests.Render();
        Assert.InRange(card.Bounds.Height, 200, 664);
        CapturePopup(scope.Get<Popup>("ReaderLayoutSettingsPopup"), "settings-small-window");
        scope.Get<Popup>("ReaderLayoutSettingsPopup").IsOpen = false;
        scope.Set("_readerActiveHost", null);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
        scope.Get<Control>("ReaderRoot").IsVisible = false;
        Assert.False(scope.Get<Control>("ReaderThemeScope").IsVisible);
        Assert.DoesNotContain("readerChrome", scope.Get<Grid>("ReaderWindowTitleBar").Classes);
    });

    [Fact]
    public Task PaperHasTonalVariationAndFibersCanBeRemovedWithoutRemovingGrain() => Run(() =>
    {
        var appearance = new ReaderAppearanceSettings { Theme = ReaderTheme.Ivory, PaperEnabled = true, PaperStrength = 0.8 };
        using var paper = PaintPaper(appearance);
        using var fibers = PaintPaper(appearance with { FibersEnabled = true });
        using var flat = PaintPaper(appearance with { PaperEnabled = false, FibersEnabled = true });
        using var zero = PaintPaper(appearance with { PaperStrength = 0, FibersEnabled = true });
        Assert.True(paper.Pixels.Distinct().Count() > 5);
        Assert.False(paper.Pixels.SequenceEqual(fibers.Pixels));
        Assert.Single(flat.Pixels.Distinct());
        Assert.True(flat.Pixels.SequenceEqual(zero.Pixels));
        foreach (var theme in Enum.GetValues<ReaderTheme>())
        {
            var palette = ReaderPalette.For(theme);
            foreach (var background in new[] { palette.Page, palette.Sidebar, palette.Chrome })
            {
                Assert.True(Contrast(palette.Ink, background) >= 7, $"{theme}: body text contrast");
                Assert.True(Contrast(palette.Muted, background) >= 4.5, $"{theme}: secondary text contrast");
            }
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task NightReaderSurfacesMenusAndAssistantUseTheReaderPalette() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await LoadChapter(scope, new());
        scope.Window.ReaderAiMessages.Add(new("user", "纸感有哪些可以独立调整？"));
        scope.Window.ReaderAiMessages.Add(new("assistant", "可以调整主题、纸感强度，也可以独立叠加纤维。"));
        scope.Call("ReaderAssistantToggleButton_Click", null, new RoutedEventArgs());
        scope.Call("ChangeReaderAppearance", new ReaderAppearanceSettings
        {
            Theme = ReaderTheme.Night,
            PaperEnabled = false,
            PaperStrength = 0.8,
            FibersEnabled = true
        });
        await scope.Field<Task>("_readerAppearanceSaveTask");
        await Task.Delay(250);
        await ReaderTests.Render();
        var palette = ReaderPalette.For(ReaderTheme.Night);
        var pageBrush = scope.Window.Resources["ReaderPageBrush"];
        var sidebarBrush = scope.Window.Resources["ReaderSidebarBrush"];
        var chromeBrush = scope.Window.Resources["ReaderChromeBrush"];
        Assert.Same(pageBrush, sidebarBrush);
        Assert.Same(pageBrush, chromeBrush);
        Assert.True(scope.Get<Border>("ReaderAssistantPanel").Bounds.Width > 200);
        Assert.Same(sidebarBrush, scope.Get<Border>("ReaderTocHeaderBar").Background);
        Assert.Same(sidebarBrush, scope.Get<Border>("ReaderAssistantHeaderBar").Background);
        Assert.Same(pageBrush, scope.Get<Border>("ReaderHeaderBar").Background);
        Assert.Same(pageBrush, scope.Get<Grid>("ReaderWindowTitleBar").Background);
        var footer = scope.Get<Border>("ReaderFooterBar");
        Assert.Same(pageBrush, footer.Background);
        Assert.Equal(new Thickness(0), footer.BorderThickness);
        Assert.Same(sidebarBrush, scope.Get<Border>("ReaderTocPanel").Background);
        Assert.Equal(palette.Ink, ((ISolidColorBrush)scope.Get<TextBlock>("ReaderBookInfoText").Foreground!).Color);
        Assert.Equal(palette.Ink, ((ISolidColorBrush)scope.Get<Button>("MinimizeWindowButton").Foreground!).Color);
        Assert.Equal(palette.Muted, ((ISolidColorBrush)scope.Get<TextBox>("ReaderAiQuestionBox").PlaceholderForeground!).Color);
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)scope.Get<Button>("MinimizeWindowButton").Background!).Color);
        Capture(scope.Window, "night-assistant");
        var more = scope.Get<Button>("ReaderMoreButton");
        more.Flyout!.ShowAt(more);
        await ReaderTests.Render();
        var menu = scope.Get<MenuItem>("ReaderZenMenuItem");
        Assert.Equal(palette.Ink, ((ISolidColorBrush)menu.Foreground!).Color);
        CapturePopupVisual(TopLevel.GetTopLevel(menu), "night-menu");
        more.Flyout.Hide();
        scope.Set("_readerActiveHost", null);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
    });

    private Task Run(Func<Task> action) => session.Session.Dispatch(async () => { await action(); return true; }, CancellationToken.None);

    internal static async Task<NativeReaderHost> LoadChapter(ReaderTestWindow scope, ReaderLayoutSettings layout)
    {
        var path = Path.Combine(scope.Paths.ReaderCache, "appearance.xhtml");
        await File.WriteAllTextAsync(path, "<html><body><h1 id='start'>纸上的时光</h1>"
            + string.Concat(Enumerable.Range(1, 100).Select(index => $"<p>第{index}段。窗外的树影慢慢移过书页，文字安静地留在纸上。阅读时，颜色与纸张的细微纹理相互衬托。The afternoon light falls softly across the page.</p>"))
            + "</body></html>");
        var host = new NativeReaderHost();
        scope.Set("_readerLayout", layout);
        scope.Set("_readerDocument", new EpubReaderDocument(scope.Paths.ReaderCache, [path], [], []));
        scope.Set("_readerTocItems", new List<EpubReaderNavigationItem> { new("纸上的时光", new Uri(path).AbsoluteUri, 0) });
        scope.Call("BuildReaderTocRows");
        scope.Call("RefreshReaderTocRows");
        scope.Get<TextBlock>("ReaderBookInfoText").Text = "纸上的时光";
        scope.Set("_readerActiveHost", host);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = host;
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ApplyReaderPanelLayout");
        await ReaderTests.Render();
        Assert.True(await scope.Call<Task<bool>>("NavigateReaderHostAndWaitAsync", host, new Uri(path), CancellationToken.None, false));
        await ReaderTests.Render();
        return host;
    }

    private static SKBitmap PaintPaper(ReaderAppearanceSettings appearance)
    {
        var bitmap = new SKBitmap(384, 256);
        using var canvas = new SKCanvas(bitmap);
        ReaderPaperTexture.Paint(canvas, new SKRect(0, 0, bitmap.Width, bitmap.Height), ReaderPalette.For(appearance.Theme).Page, appearance);
        return bitmap;
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte c) { var n = c / 255d; return n <= 0.04045 ? n / 12.92 : Math.Pow((n + 0.055) / 1.055, 2.4); }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static int ColorDistance(SKColor a, SKColor b) => Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));
    private static T NativeField<T>(NativeReaderHost host, string name) => (T)typeof(NativeReaderHost).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
    private static void NativeSet(NativeReaderHost host, string name, object value) => typeof(NativeReaderHost).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(host, value);

    private static void Capture(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("KKINDLE_READER_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var image = window.CaptureRenderedFrame();
        image!.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void CapturePopup(Popup popup, string name) => CapturePopupVisual(popup.Child, name);

    private static void CapturePopupVisual(Visual? visual, string name)
    {
        var directory = Environment.GetEnvironmentVariable("KKINDLE_READER_ARTIFACTS");
        if (visual is null || string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var image = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)visual.Bounds.Width), Math.Max(1, (int)visual.Bounds.Height)));
        image.Render(visual);
        image.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}

using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData(AppTheme.Classic, "#FFFFFF")]
    [InlineData(AppTheme.Night, "#20231F")]
    [InlineData(AppTheme.Green, "#EDF3EA")]
    [InlineData(AppTheme.WarmBrown, "#F4EBDF")]
    [InlineData(AppTheme.Ivory, "#F8F5EC")]
    public Task MainThemesUpdateExistingLibraryControlsAndPersist(AppTheme theme, string background) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var initialAppearance = scope.Field<AppSettings>("_appSettings").ReaderAppearance;
        var root = scope.Get<Grid>("LibraryRoot");
        var bookGrid = scope.Get<ListBox>("BookGrid");
        var books = bookGrid.ItemsSource;
        Assert.Equal(Colors.White, ColorOf(root.Background));
        Assert.Equal(0, scope.Get<ComboBox>("MainThemeBox").SelectedIndex);

        scope.Get<ComboBox>("MainThemeBox").SelectedIndex = (int)theme;
        await Render();
        Assert.Equal(Color.Parse(background), ColorOf(root.Background));
        Assert.Same(books, bookGrid.ItemsSource);
        Assert.Equal(theme == AppTheme.Night ? ThemeVariant.Dark : ThemeVariant.Light, scope.Window.ActualThemeVariant);
        Assert.Equal(ResourceColor("SidebarBrush"), ColorOf(scope.Get<Border>("LibrarySidebar").Background));
        Assert.Equal(ResourceColor("MutedInkBrush"), ColorOf(scope.Get<Avalonia.Controls.Shapes.Path>("S3SyncCloudIcon").Stroke));
        AssertThemeContrast();
        var search = scope.Get<TextBox>("SearchBox");
        var placeholder = search.GetVisualDescendants().OfType<TextBlock>().First(x => x.Text == search.PlaceholderText && x.IsEffectivelyVisible);
        var opacity = placeholder.GetVisualAncestors().Prepend(placeholder).Aggregate(placeholder.Foreground!.Opacity, (value, visual) => value * visual.Opacity);
        var color = ColorOf(placeholder.Foreground);
        var paper = ColorOf(root.Background);
        var painted = Color.FromRgb((byte)(color.R * opacity + paper.R * (1 - opacity)),
            (byte)(color.G * opacity + paper.G * (1 - opacity)), (byte)(color.B * opacity + paper.B * (1 - opacity)));
        Assert.True(Contrast(painted, paper) >= 4.5, $"Search placeholder {placeholder.Name}: {color}, opacity {opacity}; "
            + string.Join(", ", placeholder.GetVisualAncestors().Prepend(placeholder).Where(v => v.Opacity < 1).Select(v => $"{v.GetType().Name} {v.Opacity}")));
        Capture(scope.Window, "main-" + theme);

        // Opening details and the list after a switch must use the same palette.
        bookGrid.SelectedItem = scope.Window.ViewModel.Books[0];
        await Until(() => scope.Get<Border>("LibraryDetailPane").IsVisible);
        await Render();
        Assert.Equal(ResourceColor("PaperBrush"), ColorOf(scope.Get<Border>("LibraryDetailPane").Background));
        if (theme == AppTheme.Night) Capture(scope.Window, "night-details");
        scope.Get<Button>("LibraryDetailCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        scope.Get<MenuItem>("LibraryListViewMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Render();
        if (theme == AppTheme.Night) Capture(scope.Window, "night-list");

        Assert.True(await scope.Call<Task<bool>>("FlushAppSettingsAsync"));
        var saved = await new AppSettingsStore(scope.Paths).LoadAsync();
        Assert.Equal(theme, saved.MainTheme);
        Assert.Equal(initialAppearance, saved.ReaderAppearance);
    });

    [Fact]
    public Task ThemeAndOtherPendingPreferencesSurviveImmediateCloseAndRestart() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<ComboBox>("MainThemeBox").SelectedIndex = (int)AppTheme.Night;
        scope.Get<ComboBox>("MainThemeBox").SelectedIndex = (int)AppTheme.WarmBrown;
        scope.Get<ToggleSwitch>("GridGalleryDisplayCheck").IsChecked = true;
        var reader = new ReaderAppearanceSettings { Theme = ReaderTheme.Green, PaperEnabled = true, FibersEnabled = true };
        scope.Call("ChangeReaderAppearance", reader);
        scope.Window.Close();
        await Until(() => !scope.Window.IsVisible);
        var saved = await new AppSettingsStore(scope.Paths).LoadAsync();
        Assert.Equal(AppTheme.WarmBrown, saved.MainTheme);
        Assert.True(saved.GridGalleryDisplay);
        Assert.Equal(reader, saved.ReaderAppearance);

        await using var reopened = await TestWindow.Create(saved);
        Assert.Equal((int)AppTheme.WarmBrown, reopened.Get<ComboBox>("MainThemeBox").SelectedIndex);
        Assert.Equal(Color.Parse("#F4EBDF"), ColorOf(reopened.Get<Grid>("LibraryRoot").Background));
        Assert.Equal(ReaderTheme.Green, reopened.Field<AppSettings>("_appSettings").ReaderAppearance.Theme);
    });

    [Fact]
    public Task MainAndReaderThemeScopesRemainIndependentAcrossRepeatedChanges() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<ComboBox>("MainThemeBox").SelectedIndex = (int)AppTheme.Night;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        await Render();
        var readerScope = scope.Get<ThemeVariantScope>("ReaderThemeScope");
        var readerInk = (ISolidColorBrush)readerScope.Resources["InkBrush"]!;
        Assert.Equal(ThemeVariant.Light, readerScope.ActualThemeVariant);
        Assert.Equal(Color.Parse("#111111"), readerInk.Color);
        Assert.Equal(Colors.White, (Color)scope.Window.Resources["ReaderPageColor"]!);

        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            scope.Get<ComboBox>("MainThemeBox").SelectedIndex = (int)theme;
            await Render();
            Assert.Equal(ThemeVariant.Light, readerScope.ActualThemeVariant);
            Assert.Equal(Color.Parse("#111111"), readerInk.Color);
            Assert.Equal(Colors.White, (Color)scope.Window.Resources["ReaderPageColor"]!);
        }

        scope.Call("ChangeReaderAppearance", new ReaderAppearanceSettings { Theme = ReaderTheme.Night });
        await scope.Field<Task>("_readerAppearanceSaveTask");
        await Render();
        Assert.Equal(ThemeVariant.Dark, readerScope.ActualThemeVariant);
        Assert.Equal(ThemeVariant.Light, scope.Window.ActualThemeVariant);
        Assert.Equal(Color.Parse("#F8F5EC"), ColorOf(scope.Get<Grid>("LibraryRoot").Background));
        Assert.Equal(Color.Parse("#DDDACE"), readerInk.Color);
        scope.Get<Control>("ReaderRoot").IsVisible = false;
        await Render();
        Assert.DoesNotContain("readerChrome", scope.Get<Grid>("ReaderWindowTitleBar").Classes);
        Assert.Equal(AppTheme.Ivory, scope.Field<AppSettings>("_appSettings").MainTheme);
    });

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task NightSettingsMenusAndDialogsStayLegibleAtMinimumSize(string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Window.Width = 1024;
        scope.Window.Height = 664;
        ((Kkindle.App)Application.Current!).ApplyLanguage(language);
        scope.Get<ComboBox>("MainThemeBox").SelectedIndex = (int)AppTheme.Night;
        scope.Call("SettingsButton_Click", null, new RoutedEventArgs());
        await Render();
        var selector = scope.Get<ComboBox>("MainThemeBox");
        Assert.True(selector.IsEffectivelyVisible);
        AssertWithinWindow(selector, scope.Window);
        Assert.Equal(language == "en-US" ? "Main interface theme" : "主界面主题",
            ControlAutomationPeer.CreatePeerForElement(selector)!.GetName());
        Capture(scope.Window, language + "-night-settings");
        selector.IsDropDownOpen = true;
        await Render();
        var popup = selector.GetVisualDescendants().OfType<Popup>().Single();
        Assert.True(popup.IsOpen);
        var dropDown = popup.Child!.GetVisualDescendants().Prepend(popup.Child).OfType<Border>().First(x => x.Name == "PopupBorder");
        Assert.Equal(ResourceColor("PaperBrush"), ColorOf(dropDown.Background));
        CaptureThemePopup(popup, language + "-night-theme-menu");
        selector.IsDropDownOpen = false;

        scope.Get<TextBlock>("ConfirmationTitleText").Text = language == "en-US" ? "Theme preview" : "主题预览";
        scope.Get<TextBlock>("ConfirmationMessageText").Text = language == "en-US" ? "Dialogs follow the main interface theme." : "弹窗随主界面主题切换。";
        scope.Get<Grid>("ConfirmationOverlay").IsVisible = true;
        await Render();
        var card = scope.Get<Grid>("ConfirmationOverlay").Children.OfType<Border>().Single();
        Assert.Equal(ResourceColor("PaperBrush"), ColorOf(card.Background));
        Assert.True(Contrast(ColorOf(scope.Get<TextBlock>("ConfirmationMessageText").Foreground), ColorOf(card.Background)) >= 4.5);
        Capture(scope.Window, language + "-night-confirmation");
        scope.Get<Grid>("ConfirmationOverlay").IsVisible = false;
    });

    [Fact]
    public Task ExistingProgressWindowsRepaintWhenTheMainThemeChanges() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var progressType = typeof(MainWindow).Assembly.GetType("Kkindle.PinyinBookProgressWindow")!;
        var progress = (Window)Activator.CreateInstance(progressType, "主题测试", false)!;
        try
        {
            progress.Show(scope.Window);
            var originalBackground = progress.Background;
            var summary = progress.GetVisualDescendants().OfType<TextBlock>().First(x => x.Text == "本地注音  ·  仅本地生成");
            foreach (var theme in new[] { AppTheme.Night, AppTheme.Green, AppTheme.Classic })
            {
                scope.Get<ComboBox>("MainThemeBox").SelectedIndex = (int)theme;
                await Render();
                Assert.Same(originalBackground, progress.Background);
                Assert.Equal(ResourceColor("PaperBrush"), ColorOf(progress.Background));
                Assert.Equal(ResourceColor("MutedInkBrush"), ColorOf(summary.Foreground));
                Assert.True(Contrast(ColorOf(summary.Foreground), ColorOf(progress.Background)) >= 4.5);
            }
        }
        finally { progress.Close(); }
    });

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
    private static Color ResourceColor(string key) => ColorOf((IBrush)Application.Current!.Resources[key]!);

    private static void AssertThemeContrast()
    {
        foreach (var background in new[] { "PaperBrush", "SidebarBrush", "PanelBrush", "SoftHoverBrush", "PressedBrush" })
        {
            Assert.True(Contrast(ResourceColor("InkBrush"), ResourceColor(background)) >= 7, background);
            Assert.True(Contrast(ResourceColor("MutedInkBrush"), ResourceColor(background)) >= 4.5, background);
        }
        Assert.True(Contrast(ResourceColor("OnAccentBrush"), ResourceColor("AccentBrush")) >= 4.5);
    }

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte value) => value / 255d <= 0.04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + 0.055) / 1.055, 2.4);
        static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void CaptureThemePopup(Popup popup, string name)
    {
        var output = Environment.GetEnvironmentVariable("KKINDLE_SETTINGS_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(output) || popup.Child is not { } child) return;
        Directory.CreateDirectory(output);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(child.Bounds.Width), (int)Math.Ceiling(child.Bounds.Height)));
        bitmap.Render(child);
        bitmap.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}

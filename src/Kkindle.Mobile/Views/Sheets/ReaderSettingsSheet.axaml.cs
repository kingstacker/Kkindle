using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;

namespace Kkindle.Mobile.Views.Sheets;

/// <summary>Reader paper and typography; PDF pages only offer paper.</summary>
public partial class ReaderSettingsSheet : UserControl
{
    private const double FontStep = 0.1;
    private const double NarrowMargin = 16;
    private const double NormalMargin = 24;
    private const double WideMargin = 36;
    private const double TightLine = 1.5;
    private const double NormalLine = 1.75;
    private const double LooseLine = 2.1;

    private readonly MobilePreferencesStore? _preferences;
    private readonly Action? _changed;

    /// <summary>Design-time only.</summary>
    public ReaderSettingsSheet() => InitializeComponent();

    public ReaderSettingsSheet(MobilePreferencesStore preferences, bool textOptions, Action changed)
    {
        _preferences = preferences;
        _changed = changed;
        InitializeComponent();
        TextOptions.IsVisible = textOptions;
        PdfOptions.IsVisible = !textOptions;
        Present();
    }

    private void Present()
    {
        var current = _preferences!.Current;
        PaperWhite.Classes.Set("on", current.ReaderTheme == ReaderTheme.Classic);
        PaperBeige.Classes.Set("on", current.ReaderTheme == ReaderTheme.Beige);
        PaperInk.Classes.Set("on", current.ReaderTheme == ReaderTheme.Ink);
        AnimationNone.Classes.Set("on", current.ReaderPageAnimation == MobilePageAnimation.None);
        AnimationFade.Classes.Set("on", current.ReaderPageAnimation == MobilePageAnimation.Fade);
        AnimationSlide.Classes.Set("on", current.ReaderPageAnimation == MobilePageAnimation.Slide);
        AnimationWave.Classes.Set("on", current.ReaderPageAnimation == MobilePageAnimation.Wave);
        ZoomPage.Classes.Set("on", current.PdfZoom <= 1);
        ZoomMedium.Classes.Set("on", current.PdfZoom is > 1 and < 2);
        ZoomLarge.Classes.Set("on", current.PdfZoom >= 2);
        FontScaleText.Text = $"{current.ReaderFontScale * 100:0}%";
        LineTight.Classes.Set("on", current.ReaderLineHeight <= TightLine);
        LineNormal.Classes.Set("on", current.ReaderLineHeight is > TightLine and < LooseLine);
        LineLoose.Classes.Set("on", current.ReaderLineHeight >= LooseLine);
        DirectionHorizontal.Classes.Set("on", !current.ReaderVerticalWriting);
        DirectionVertical.Classes.Set("on", current.ReaderVerticalWriting);
        MarginNarrow.Classes.Set("on", current.ReaderBodyPadding <= NarrowMargin);
        MarginNormal.Classes.Set("on", current.ReaderBodyPadding is > NarrowMargin and < WideMargin);
        MarginWide.Classes.Set("on", current.ReaderBodyPadding >= WideMargin);
        IndentOff.Classes.Set("on", !current.ReaderParagraphIndent);
        IndentOn.Classes.Set("on", current.ReaderParagraphIndent);
    }

    private void Change(Func<MobilePreferences, MobilePreferences> change)
    {
        _preferences!.Update(change);
        Present();
        _changed?.Invoke();
    }

    private void ChangeFont(double delta) => Change(value => value with
    {
        ReaderFontScale = Math.Clamp(
            Math.Round(value.ReaderFontScale + delta, 2),
            ReaderLayoutDefaults.MinFontScale,
            ReaderLayoutDefaults.MaxFontScale)
    });

    private void OnSmallerClick(object? sender, RoutedEventArgs e) => ChangeFont(-FontStep);

    private void OnLargerClick(object? sender, RoutedEventArgs e) => ChangeFont(FontStep);

    private void OnPaperClick(object? sender, RoutedEventArgs e)
    {
        var theme = sender switch
        {
            Button { Name: nameof(PaperBeige) } => ReaderTheme.Beige,
            Button { Name: nameof(PaperInk) } => ReaderTheme.Ink,
            _ => ReaderTheme.Classic
        };
        Change(value => value with { ReaderTheme = theme });
    }

    private void OnZoomClick(object? sender, RoutedEventArgs e)
    {
        var zoom = sender switch
        {
            Button { Name: nameof(ZoomMedium) } => 1.5,
            Button { Name: nameof(ZoomLarge) } => 2d,
            _ => 1d
        };
        Change(value => value with { PdfZoom = zoom });
    }

    private void OnAnimationClick(object? sender, RoutedEventArgs e)
    {
        var animation = sender switch
        {
            Button { Name: nameof(AnimationNone) } => MobilePageAnimation.None,
            Button { Name: nameof(AnimationSlide) } => MobilePageAnimation.Slide,
            Button { Name: nameof(AnimationWave) } => MobilePageAnimation.Wave,
            _ => MobilePageAnimation.Fade
        };
        Change(value => value with { ReaderPageAnimation = animation });
    }

    private void OnDirectionClick(object? sender, RoutedEventArgs e)
    {
        var vertical = sender is Button { Name: nameof(DirectionVertical) };
        Change(value => value with { ReaderVerticalWriting = vertical });
    }

    private void OnMarginClick(object? sender, RoutedEventArgs e)
    {
        var padding = sender switch
        {
            Button { Name: nameof(MarginNarrow) } => NarrowMargin,
            Button { Name: nameof(MarginWide) } => WideMargin,
            _ => NormalMargin
        };
        Change(value => value with { ReaderBodyPadding = padding });
    }

    private void OnIndentClick(object? sender, RoutedEventArgs e)
    {
        var indent = sender is Button { Name: nameof(IndentOn) };
        Change(value => value with { ReaderParagraphIndent = indent });
    }

    private void OnLineClick(object? sender, RoutedEventArgs e)
    {
        var height = sender switch
        {
            Button { Name: nameof(LineTight) } => TightLine,
            Button { Name: nameof(LineLoose) } => LooseLine,
            _ => NormalLine
        };
        Change(value => value with { ReaderLineHeight = height });
    }
}

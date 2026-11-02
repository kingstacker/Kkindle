using System.Runtime.Versioning;
using Foundation;
using Kkindle.Mobile;
using UIKit;

namespace Kkindle.iOSHead;

/// <summary>
/// The real Liquid Glass tab bar. Avalonia cannot render <c>UIGlassEffect</c>,
/// so on iOS 26 the bar is a native <see cref="UIVisualEffectView"/> layered on
/// the window above Avalonia's view, with UIKit buttons inside it. The shell
/// keeps its own bar invisible so the layout still reserves the same space.
/// </summary>
[SupportedOSPlatform("ios26.0")]
internal sealed class IOSGlassTabBar : IMobileTabBar
{
    private static readonly string[] Titles = ["书库", "阅读资料", "阅读数据", "我的"];
    private static readonly string[] Symbols = ["book", "note.text", "chart.bar", "person"];

    private readonly UIVisualEffectView _glass;
    private readonly UIButton[] _buttons;
    private bool _visible;
    private int _selected;

    public IOSGlassTabBar()
    {
        var effect = UIGlassEffect.Create(UIGlassEffectStyle.Regular);
        effect.Interactive = true;

        _glass = new UIVisualEffectView(effect)
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            Hidden = true,
            CornerConfiguration = UICornerConfiguration.CreateCapsule()
        };

        var stack = new UIStackView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            Axis = UILayoutConstraintAxis.Horizontal,
            Distribution = UIStackViewDistribution.FillEqually,
            Alignment = UIStackViewAlignment.Fill
        };
        _glass.ContentView.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints([
            stack.LeadingAnchor.ConstraintEqualTo(_glass.ContentView.LeadingAnchor, 6),
            stack.TrailingAnchor.ConstraintEqualTo(_glass.ContentView.TrailingAnchor, -6),
            stack.TopAnchor.ConstraintEqualTo(_glass.ContentView.TopAnchor, 3),
            stack.BottomAnchor.ConstraintEqualTo(_glass.ContentView.BottomAnchor, -3)
        ]);

        _buttons = new UIButton[Titles.Length];
        for (var index = 0; index < Titles.Length; index++)
        {
            var selected = index;
            var button = new UIButton(UIButtonType.System)
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                AccessibilityLabel = Titles[index]
            };
            button.TouchUpInside += (_, _) => TabSelected?.Invoke(selected);
            _buttons[index] = button;
            stack.AddArrangedSubview(button);
        }

        UpdateButtons();
    }

    public event Action<int>? TabSelected;

    public void Update(bool visible, int selectedIndex)
    {
        _visible = visible;
        _selected = selectedIndex;
        UpdateButtons();
        Apply();
    }

    /// <summary>
    /// Called once the app is active, when the window and Avalonia's view exist.
    /// Safe to call repeatedly; it re-fronts the bar and reattaches after a
    /// window change.
    /// </summary>
    public void Attach()
    {
        Apply();
        var window = KeyWindow();
        if (window is null) return;

        if (!ReferenceEquals(_glass.Superview, window))
        {
            _glass.RemoveFromSuperview();
            window.AddSubview(_glass);
            NSLayoutConstraint.ActivateConstraints([
                _glass.LeadingAnchor.ConstraintEqualTo(window.SafeAreaLayoutGuide.LeadingAnchor, 12),
                _glass.TrailingAnchor.ConstraintEqualTo(window.SafeAreaLayoutGuide.TrailingAnchor, -12),
                _glass.BottomAnchor.ConstraintEqualTo(window.SafeAreaLayoutGuide.BottomAnchor, -8),
                _glass.HeightAnchor.ConstraintEqualTo(60)
            ]);
        }

        window.BringSubviewToFront(_glass);
        _glass.Hidden = !_visible;
    }

    private void Apply() => _glass.Hidden = !_visible;

    private void UpdateButtons()
    {
        for (var index = 0; index < _buttons.Length; index++)
        {
            var active = index == _selected;
            var foreground = active ? UIColor.Label : UIColor.SecondaryLabel;
            var background = UIBackgroundConfiguration.ClearConfiguration;
            background.BackgroundColor = active ? UIColor.TertiarySystemFill : UIColor.Clear;
            background.BackgroundInsets = new NSDirectionalEdgeInsets(2, 2, 2, 2);
            background.CornerRadius = 26;

            var configuration = UIButtonConfiguration.PlainButtonConfiguration;
            configuration.AttributedTitle = new NSAttributedString(Titles[index], new UIStringAttributes
            {
                Font = UIFont.SystemFontOfSize(11, active ? UIFontWeight.Semibold : UIFontWeight.Medium),
                ForegroundColor = foreground
            });
            configuration.Image = UIImage.GetSystemImage(
                Symbols[index],
                UIImageSymbolConfiguration.Create(22, UIImageSymbolWeight.Regular));
            configuration.ImagePlacement = NSDirectionalRectEdge.Top;
            configuration.ImagePadding = 3;
            configuration.ContentInsets = new NSDirectionalEdgeInsets(5, 0, 5, 0);
            configuration.BaseForegroundColor = foreground;
            configuration.Background = background;
            _buttons[index].Configuration = configuration;
        }
    }

    private static UIWindow? KeyWindow()
    {
        var windows = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .SelectMany(scene => scene.Windows)
            .ToArray();
        return windows.FirstOrDefault(window => window.IsKeyWindow) ?? windows.FirstOrDefault();
    }
}

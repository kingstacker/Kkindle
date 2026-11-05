using System.Runtime.Versioning;
using Kkindle.Mobile;
using UIKit;

namespace Kkindle.iOSHead;

/// <summary>System tab bar: UIKit supplies the iOS 26 Liquid Glass appearance.</summary>
[SupportedOSPlatform("ios26.0")]
internal sealed class IOSGlassTabBar : IMobileTabBar
{
    private static readonly string[] Titles = ["书库", "阅读资料", "阅读数据", "我的"];
    private static readonly string[] Symbols = ["book", "note.text", "chart.bar", "person"];
    private readonly UITabBar _bar;
    private readonly UITabBarItem[] _items;
    private bool _visible;
    private int _selected;

    public IOSGlassTabBar()
    {
        _items = Titles.Select((title, index) =>
            new UITabBarItem(title, UIImage.GetSystemImage(Symbols[index]), index)).ToArray();
        if (UIFont.FromName("KingHwaOldSong", 10) is { } font)
        {
            var attributes = new UIStringAttributes { Font = font };
            foreach (var item in _items)
            {
                item.SetTitleTextAttributes(attributes, UIControlState.Normal);
                item.SetTitleTextAttributes(attributes, UIControlState.Selected);
            }
        }
        _bar = new UITabBar
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            Hidden = true,
            Items = _items,
            TintColor = UIColor.Label,
            UnselectedItemTintColor = UIColor.SecondaryLabel
        };
        _bar.ItemSelected += (_, args) =>
        {
            var index = Array.IndexOf(_items, args.Item);
            if (index >= 0) TabSelected?.Invoke(index);
        };
        _bar.SelectedItem = _items[0];
    }

    public event Action<int>? TabSelected;

    public void Update(bool visible, int selectedIndex)
    {
        _visible = visible;
        _selected = Math.Clamp(selectedIndex, 0, _items.Length - 1);
        UIView.PerformWithoutAnimation(() =>
        {
            if (_bar.SelectedItem != _items[_selected])
                _bar.SelectedItem = _items[_selected];
            _bar.Hidden = !_visible;
        });
    }

    public void SetDarkAppearance(bool dark) =>
        _bar.OverrideUserInterfaceStyle = dark ? UIUserInterfaceStyle.Dark : UIUserInterfaceStyle.Light;

    public void Attach()
    {
        var window = KeyWindow();
        if (window is null) return;
        if (!ReferenceEquals(_bar.Superview, window))
        {
            _bar.RemoveFromSuperview();
            window.AddSubview(_bar);
            NSLayoutConstraint.ActivateConstraints([
                _bar.LeadingAnchor.ConstraintEqualTo(window.LeadingAnchor),
                _bar.TrailingAnchor.ConstraintEqualTo(window.TrailingAnchor),
                _bar.BottomAnchor.ConstraintEqualTo(window.BottomAnchor),
                _bar.TopAnchor.ConstraintEqualTo(window.SafeAreaLayoutGuide.BottomAnchor, -49)
            ]);
        }
        window.BringSubviewToFront(_bar);
        Update(_visible, _selected);
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

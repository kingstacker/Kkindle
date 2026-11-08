using Avalonia;

namespace Kkindle.Mobile;

/// <summary>
/// A tab bar drawn by the platform instead of Avalonia. iOS 26 uses it for the
/// system Liquid Glass material, which cannot be rendered inside a view tree
/// Avalonia draws itself. Indices follow the shell's tab order: 书库, 阅读资料,
/// 阅读数据, 我的.
/// </summary>
public interface IMobileTabBar
{
    /// <summary>Raised when the host bar selects a tab.</summary>
    event Action<int>? TabSelected;

    /// <summary>
    /// Keeps the native bar in step with the shell. <paramref name="visible"/>
    /// is false while a pushed page owns the screen.
    /// </summary>
    void Update(bool visible, int selectedIndex);

    /// <summary>Matches the native bar to the application's current appearance.</summary>
    void SetDarkAppearance(bool dark);
}

public interface ISheetOccludingTabBar
{
    void SetSheetOcclusion(Rect? sheetBounds);
}

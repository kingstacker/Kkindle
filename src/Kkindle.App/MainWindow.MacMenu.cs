using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    /// <summary>
    /// Installs the standard macOS menu bar. Without an application menu,
    /// Avalonia falls back to its own menu containing "About Avalonia", which
    /// is both unlocalized and unrelated to Kkindle.
    /// </summary>
    internal void InitializeMacMenu()
    {
        if (!MacTrafficLights.IsSupported) return;
        BuildMacMenus();
        UiText.LanguageChanged += MacMenuLanguageChanged;
    }

    private void MacMenuLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) BuildMacMenus();
        else Dispatcher.UIThread.Post(BuildMacMenus);
    }

    private void BuildMacMenus()
    {
        if (Application.Current is not { } application) return;

        var applicationItems = new NativeMenu();
        var applicationMenu = new NativeMenuItem("Kkindle")
        {
            Menu = applicationItems
        };
        applicationItems.Items.Add(MacMenuItem(
            T("关于 Kkindle"),
            ShowAboutFromMacMenu));
        applicationItems.Items.Add(new NativeMenuItemSeparator());
        applicationItems.Items.Add(MacMenuItem(
            T("设置…"),
            ShowSettingsFromMacMenu,
            new KeyGesture(Key.OemComma, KeyModifiers.Meta)));

        var menu = new NativeMenu();
        menu.Items.Add(applicationMenu);
        menu.Items.Add(BuildMacFileMenu());
        menu.Items.Add(BuildMacWindowMenu());
        menu.Items.Add(BuildMacHelpMenu());
        // Avalonia 12's macOS exporter observes NativeDock.MenuProperty on
        // Application; NativeMenu.SetMenu only routes TopLevel/window menus.
        NativeDock.SetMenu(application, menu);
    }

    private NativeMenuItem BuildMacFileMenu()
    {
        var items = new NativeMenu();
        items.Items.Add(MacMenuItem(
            T("导入书籍…"),
            ImportBooksFromMacMenu,
            new KeyGesture(Key.I, KeyModifiers.Meta)));
        items.Items.Add(new NativeMenuItemSeparator());
        // Close goes through the normal window closing path, which hides the
        // window instead of quitting when the app keeps running in the menu bar.
        items.Items.Add(MacMenuItem(
            T("关闭窗口"),
            Close,
            new KeyGesture(Key.W, KeyModifiers.Meta)));
        return new NativeMenuItem(T("文件")) { Menu = items };
    }

    private NativeMenuItem BuildMacWindowMenu()
    {
        var items = new NativeMenu();
        items.Items.Add(MacMenuItem(
            T("最小化"),
            () => WindowState = WindowState.Minimized,
            new KeyGesture(Key.M, KeyModifiers.Meta)));
        items.Items.Add(MacMenuItem(
            T("切换全屏"),
            () => WindowState = WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState.FullScreen,
            new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Meta)));
        return new NativeMenuItem(T("窗口")) { Menu = items };
    }

    private NativeMenuItem BuildMacHelpMenu()
    {
        var items = new NativeMenu();
        items.Items.Add(MacMenuItem(
            T("键盘快捷键"),
            () => { _ = ShowKeyboardShortcutsAsync(); },
            new KeyGesture(Key.F1)));
        items.Items.Add(MacMenuItem(
            T("用户手册"),
            () => OpenUserManualButton_Click(this, new RoutedEventArgs())));
        return new NativeMenuItem(T("帮助")) { Menu = items };
    }

    private static NativeMenuItem MacMenuItem(string header, Action action, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowAboutFromMacMenu()
    {
        RestoreFromTray();
        SettingsButton_Click(this, new RoutedEventArgs());
        SettingsSearchBox.Text = string.Empty;
        ShowSettingsSection("About");
        SettingsScrollViewer.Offset = default;
    }

    private void ShowSettingsFromMacMenu()
    {
        RestoreFromTray();
        SettingsButton_Click(this, new RoutedEventArgs());
    }

    private void ImportBooksFromMacMenu()
    {
        RestoreFromTray();
        ImportFilesButton_Click(this, new RoutedEventArgs());
    }
}

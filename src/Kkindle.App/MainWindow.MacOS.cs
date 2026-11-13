using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private bool _macOSWindowCloseInProgress;
    private NativeMenu? _macOSApplicationMenu;
    private NativeMenuItem? _macOSSettingsMenuItem;

    private void InitializeMacOSWindow()
    {
        if (!OperatingSystem.IsMacOS()) return;
        DesktopPlatform.ApplyNativeWindowChrome(this, reserveTitleBarSpace: false);
        if (Content is Panel windowLayer)
            windowLayer.Children.Add(new MacOSWindowDragRegion(this, WindowShadowHost));
        Background = AppAppearanceResources.GetBrush("PaperBrush");
        ReaderWindowTitleBar.IsVisible = false;
        WindowShadowHost.BorderThickness = new Thickness(0);
        WindowFrameOverlay.IsVisible = false;
        WindowResizeLayer.IsVisible = false;
        foreach (var panel in new Control[]
        {
            ReaderTocPanel, ReaderTocCompactPanel, ReaderContentPanel, ReaderAssistantPanel
        })
            panel.Margin = new Thickness(0, DesktopPlatform.ReaderTitleBarInset, 0, 0);
        // The sidebar paints all the way to the top; only its contents move
        // down to leave a safe hit area for the native traffic lights.
        if (LibrarySidebar.Child is Grid sidebar)
            sidebar.Margin = new Thickness(18, 44, 18, 18);
        ReaderZenPopupMinimizeButton.IsVisible = false;
        ReaderZenPopupMaximizeButton.IsVisible = false;
        ReaderZenPopupCloseButton.IsVisible = false;

        BuildMacOSMenus();
        UiText.LanguageChanged += MacOSMenuLanguageChanged;
        Closed += (_, _) =>
        {
            UiText.LanguageChanged -= MacOSMenuLanguageChanged;
            if (_macOSSettingsMenuItem is { } settings)
                _macOSApplicationMenu?.Items.Remove(settings);
        };
    }

    private void MacOSMenuLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) BuildMacOSMenus();
        else Dispatcher.UIThread.Post(BuildMacOSMenus);
    }

    private void BuildMacOSMenus()
    {
        NativeMenuItem Item(string title, Action action, Key key, KeyModifiers modifiers = KeyModifiers.Meta)
        {
            var item = new NativeMenuItem(title) { Gesture = new KeyGesture(key, modifiers) };
            item.Click += (_, _) => action();
            return item;
        }

        var file = new NativeMenu();
        var import = Item(T("导入书籍"), () => ImportFilesButton_Click(this, new RoutedEventArgs()), Key.I);
        file.Items.Add(import);
        file.NeedsUpdate += (_, _) => import.IsEnabled = !ReaderRoot.IsVisible && !OnboardingOverlay.IsVisible;
        file.Items.Add(new NativeMenuItemSeparator());
        file.Items.Add(Item(T("关闭"), () => { _ = HideMacOSWindowAsync(); }, Key.W));
        var view = new NativeMenu();
        var fullscreen = Item(T("全屏"), ToggleMacOSFullScreen, Key.F, KeyModifiers.Control | KeyModifiers.Meta);
        var zen = new NativeMenuItem(T("禅模式")) { ToggleType = MenuItemToggleType.CheckBox };
        zen.Click += (_, _) => ToggleReaderZenMode();
        view.Items.Add(fullscreen);
        view.Items.Add(zen);
        view.NeedsUpdate += (_, _) =>
        {
            fullscreen.Header = T(WindowState == WindowState.FullScreen ? "退出全屏" : "全屏");
            zen.IsEnabled = ReaderRoot.IsVisible;
            zen.IsChecked = _readerZenMode;
        };
        var window = new NativeMenu();
        window.Items.Add(Item(T("最小化"), () => WindowState = WindowState.Minimized, Key.M));
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem(T("文件")) { Menu = file });
        menu.Items.Add(new NativeMenuItem(T("视图")) { Menu = view });
        menu.Items.Add(new NativeMenuItem(T("窗口")) { Menu = window });
        var help = new NativeMenu();
        var shortcuts = new NativeMenuItem(T("键盘快捷键"));
        shortcuts.Click += (_, _) => { _ = ShowKeyboardShortcutsAsync(); };
        help.Items.Add(shortcuts);
        menu.Items.Add(new NativeMenuItem(T("帮助")) { Menu = help });
        NativeMenu.SetMenu(this, menu);
        ToolTip.SetTip(ReaderSearchToolbarButton, DesktopPlatform.ShortcutLabel(T("搜索整本书（Ctrl+F）")));
        ToolTip.SetTip(ReaderAiQuestionBox, DesktopPlatform.ShortcutLabel(T("支持粘贴图片 · Ctrl+V")));

        if (Application.Current is { } app)
        {
            // Avalonia exports the application menu at startup. Mutate that
            // instance so the native exporter observes our settings item.
            var applicationMenu = NativeMenu.GetMenu(app) ?? new NativeMenu();
            if (_macOSSettingsMenuItem is { } previous) applicationMenu.Items.Remove(previous);
            foreach (var defaultAbout in applicationMenu.Items.OfType<NativeMenuItem>()
                         .Where(item => item.Header == "About Avalonia").ToArray())
                applicationMenu.Items.Remove(defaultAbout);
            _macOSSettingsMenuItem = Item(T("设置") + "…", async () =>
            {
                RestoreFromTray();
                if (ReaderRoot.IsVisible) await CloseReaderAsync();
                if (!ReaderRoot.IsVisible) SettingsButton_Click(this, new RoutedEventArgs());
            }, Key.OemComma);
            applicationMenu.Items.Insert(0, _macOSSettingsMenuItem);
            _macOSApplicationMenu = applicationMenu;
            NativeMenu.SetMenu(app, applicationMenu);
        }
    }

    private void ToggleMacOSFullScreen()
    {
        if (_readerZenMode)
            ExitReaderZenMode();
        else
            WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }

    internal void ReopenMacOSWindow() => RestoreFromTray();

    private async Task HideMacOSWindowAsync()
    {
        if (_backupImportInProgress)
        {
            SetTaskStatus(T("备份正在导入，请等待完成后再退出。"));
            return;
        }
        if (_macOSWindowCloseInProgress) return;
        _macOSWindowCloseInProgress = true;
        try
        {
            if (ReaderRoot.IsVisible) await CloseReaderAsync();
            if (await FlushAppSettingsAsync()) Hide();
        }
        finally
        {
            _macOSWindowCloseInProgress = false;
        }
    }

    private async Task QuitMacOSAfterReaderAsync()
    {
        if (_macOSWindowCloseInProgress) return;
        _macOSWindowCloseInProgress = true;
        try
        {
            await CloseReaderAsync();
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.TryShutdown();
        }
        finally
        {
            _macOSWindowCloseInProgress = false;
        }
    }
}

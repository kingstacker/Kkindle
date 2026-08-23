using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;

namespace Kkindle;

public partial class MainWindow
{
    // Tray presence: the app is single-instance, so the tray icon doubles as
    // the recovery surface when the window is minimized into it.
    private TrayIcon? _trayIcon;

    private void InitializeTrayIcon()
    {
        try
        {
            var icon = new WindowIcon(AssetLoader.Open(
                new Uri("avares://Kkindle.App/Assets/Icons/kkindle.png")));

            var openItem = new NativeMenuItem("打开 Kkindle");
            openItem.Click += (_, _) => RestoreFromTray();
            var quitItem = new NativeMenuItem("退出");
            quitItem.Click += (_, _) =>
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                    ?.Shutdown();

            var menu = new NativeMenu();
            menu.Items.Add(openItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quitItem);

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "Kkindle",
                Menu = menu,
                IsVisible = true
            };
            // A left click also brings the window back, matching common
            // Windows tray behaviour.
            _trayIcon.Clicked += (_, _) => RestoreFromTray();
            if (Application.Current is { } app)
                TrayIcon.SetIcons(app, [_trayIcon]);
            else
                _trayIcon = null;
        }
        catch
        {
            // A missing tray icon must not prevent startup either.
            _trayIcon = null;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }
}

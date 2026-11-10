using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    // Tray presence: the app is single-instance, so the tray icon doubles as
    // the recovery surface when the window is minimized into it.
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayOpenItem;
    private NativeMenuItem? _trayQuitItem;

    private void InitializeTrayIcon()
    {
        try
        {
            // macOS menu bar icons are template images: only the alpha channel
            // is used and the system tints them for light/dark menu bars. The
            // full app icon is an opaque square, which would render as a solid
            // block, so the menu bar uses a transparent glyph instead.
            var icon = OperatingSystem.IsMacOS()
                ? CreateMacTrayIcon()
                : new WindowIcon(AssetLoader.Open(new Uri("avares://Kkindle.App/Assets/Icons/kkindle.png")));

            var openItem = new NativeMenuItem(T("打开 Kkindle"));
            openItem.Click += (_, _) => RestoreFromTray();
            var quitItem = new NativeMenuItem(T("退出"));
            quitItem.Click += (_, _) => RequestApplicationQuit();

            var menu = new NativeMenu();
            menu.Items.Add(openItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quitItem);

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = T("Kkindle"),
                Menu = menu,
                IsVisible = true
            };
            if (OperatingSystem.IsMacOS())
                MacOSProperties.SetIsTemplateIcon(_trayIcon, true);
            // A left click also brings the window back, matching common
            // Windows tray behaviour.
            _trayIcon.Clicked += (_, _) => RestoreFromTray();
            _trayOpenItem = openItem;
            _trayQuitItem = quitItem;
            UiText.LanguageChanged += TrayLanguageChanged;
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

    private static WindowIcon CreateMacTrayIcon()
    {
        var uri = new Uri("avares://Kkindle.App/Assets/Icons/tray.png");
        try
        {
            using var source = new Bitmap(AssetLoader.Open(uri));
            using var enlarged = new RenderTargetBitmap(new PixelSize(128, 128));
            using (var drawing = enlarged.CreateDrawingContext())
            {
                // The source has generous transparent padding. Keep a small
                // margin while using more of the menu bar's available height.
                var sourceSize = source.Size;
                drawing.DrawImage(source,
                    new Rect(sourceSize.Width * 14 / 128, sourceSize.Height * 14 / 128,
                        sourceSize.Width * 100 / 128, sourceSize.Height * 100 / 128),
                    new Rect(0, 0, 128, 128));
            }
            using var stream = new MemoryStream();
            enlarged.Save(stream, PngBitmapEncoderOptions.Default);
            stream.Position = 0;
            return new WindowIcon(stream);
        }
        catch
        {
            // Retain a usable tray icon if the platform cannot render a bitmap.
            return new WindowIcon(AssetLoader.Open(uri));
        }
    }

    private void TrayLanguageChanged(object? sender, EventArgs e)
    {
        if (_trayOpenItem is not null) _trayOpenItem.Header = T("打开 Kkindle");
        if (_trayQuitItem is not null) _trayQuitItem.Header = T("退出");
        if (_trayIcon is not null) _trayIcon.ToolTipText = T("Kkindle");
    }

    private void RestoreFromTray()
    {
        var wasHidden = !IsVisible;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        else if (wasHidden)
            BeginWindowRestoreAnimation();
        Activate();
    }
}

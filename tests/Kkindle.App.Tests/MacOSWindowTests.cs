using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.VisualTree;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task WindowHeaderDragIncludesTitlesButPreservesControlsAndFullscreen() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var title = scope.Get<Control>("LibraryHeader");
        var headerPoint = new Point(400, 60);
        const double headerHeight = DesktopPlatform.MainWindowDragHeaderHeight;
        Assert.True(DesktopPlatform.IsWindowDragArea(scope.Window, title, headerPoint, headerHeight));
        Assert.True(DesktopPlatform.IsWindowDragArea(scope.Window, scope.Get<Control>("LibraryRoot"),
            new Point(800, 24), headerHeight));

        var sync = scope.Get<Button>("S3SyncIndicatorButton");
        sync.Focusable = false;
        var syncPoint = sync.TranslatePoint(new Point(sync.Bounds.Width / 2, sync.Bounds.Height / 2), scope.Window)!.Value;
        Assert.InRange(syncPoint.Y, 0, headerHeight - 1);
        Assert.False(DesktopPlatform.IsWindowDragArea(scope.Window, sync, syncPoint, headerHeight));
        Assert.False(DesktopPlatform.IsWindowDragArea(scope.Window, scope.Get<TextBox>("SearchBox"), headerPoint, headerHeight));
        Assert.False(DesktopPlatform.IsWindowDragArea(scope.Window, scope.Get<ListBox>("BookGrid"), headerPoint, headerHeight));
        Assert.False(DesktopPlatform.IsWindowDragArea(scope.Window, title, new Point(400, 160), headerHeight));

        if (OperatingSystem.IsMacOS())
        {
            var nativeRegion = Assert.Single(scope.Window.GetVisualDescendants().OfType<MacOSWindowDragRegion>());
            Assert.Equal(WindowDecorationsElementRole.TitleBar, WindowDecorationProperties.GetElementRole(nativeRegion));
            Assert.True(nativeRegion.HitTest(scope.Window.TranslatePoint(headerPoint, nativeRegion)!.Value));
            Assert.Same(nativeRegion, scope.Window.InputHitTest(headerPoint));
            Assert.False(nativeRegion.HitTest(scope.Window.TranslatePoint(syncPoint, nativeRegion)!.Value));
            Assert.NotSame(nativeRegion, scope.Window.InputHitTest(syncPoint));
            sync.IsEnabled = false;
            await Render();
            Assert.False(nativeRegion.HitTest(scope.Window.TranslatePoint(syncPoint, nativeRegion)!.Value));
        }

        scope.Window.WindowState = WindowState.FullScreen;
        Assert.False(DesktopPlatform.IsWindowDragArea(scope.Window, title, headerPoint, headerHeight));
        scope.Window.WindowState = WindowState.Normal;
    });

    [Fact]
    public Task MacOSUsesNativeFrameAndTemplateStatusIcon() => Run(async () =>
    {
        if (!OperatingSystem.IsMacOS()) return;
        await using var scope = await TestWindow.Create();
        Assert.Equal(WindowDecorations.Full, scope.Window.WindowDecorations);
        Assert.True(scope.Window.ExtendClientAreaToDecorationsHint);
        Assert.Equal(38, scope.Window.ExtendClientAreaTitleBarHeightHint);
        Assert.Equal(new Thickness(0), scope.Window.Padding);
        Assert.False(scope.Get<Control>("ReaderWindowTitleBar").IsVisible);
        Assert.False(scope.Get<Control>("WindowResizeLayer").IsVisible);
        Assert.False(scope.Get<Control>("WindowFrameOverlay").IsVisible);
        Assert.Equal(new Thickness(0), scope.Get<Border>("WindowShadowHost").BorderThickness);
        Assert.Equal(new Thickness(0, 38, 0, 0), scope.Get<Control>("ReaderContentPanel").Margin);
        var tray = scope.Field<TrayIcon>("_trayIcon");
        Assert.True(MacOSProperties.GetIsTemplateIcon(tray));
        Assert.NotNull(NativeMenu.GetMenu(scope.Window));
        var applicationMenu = NativeMenu.GetMenu(Application.Current!)!;
        scope.Call("BuildMacOSMenus");
        Assert.Same(applicationMenu, NativeMenu.GetMenu(Application.Current!));
        Assert.Single(applicationMenu.Items.OfType<NativeMenuItem>(), item => item.Header == "设置…");
        Assert.DoesNotContain(applicationMenu.Items.OfType<NativeMenuItem>(), item => item.Header == "About Avalonia");
    });

    [Fact]
    public Task MacOSZenPreservesNativeFrameAndRestoresAfterNativeFullscreenExit() => Run(async () =>
    {
        if (!OperatingSystem.IsMacOS()) return;
        await using var scope = await TestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        var extended = scope.Window.ExtendClientAreaToDecorationsHint;
        var titleBarHeight = scope.Window.ExtendClientAreaTitleBarHeightHint;
        scope.Call("ToggleReaderZenMode");
        Assert.Equal(WindowState.FullScreen, scope.Window.WindowState);
        Assert.Equal(WindowDecorations.Full, scope.Window.WindowDecorations);
        Assert.False(scope.Get<Control>("ReaderHeaderBar").IsVisible);
        scope.Window.WindowState = WindowState.Normal;
        Assert.False(scope.Field<bool>("_readerZenMode"));
        Assert.True(scope.Get<Control>("ReaderHeaderBar").IsVisible);
        Assert.False(scope.Get<Control>("ReaderWindowTitleBar").IsVisible);
        Assert.Equal(new Thickness(0, 38, 0, 0), scope.Get<Control>("ReaderContentPanel").Margin);
        Assert.Equal(extended, scope.Window.ExtendClientAreaToDecorationsHint);
        Assert.Equal(titleBarHeight, scope.Window.ExtendClientAreaTitleBarHeightHint);

        // Entering zen from an already fullscreen reader must return to that
        // Space; leaving it with the native control must restore the panels.
        scope.Window.WindowState = WindowState.FullScreen;
        scope.Call("ToggleReaderZenMode");
        scope.Call("ExitReaderZenMode");
        Assert.Equal(WindowState.FullScreen, scope.Window.WindowState);
        Assert.Equal(WindowDecorations.Full, scope.Window.WindowDecorations);
        scope.Window.WindowState = WindowState.Normal;
        scope.Get<Control>("ReaderRoot").IsVisible = false;
    });

    [Fact]
    public Task PlatformCommandModifierSelectsLibraryBooks() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        var select = new KeyEventArgs { Key = Key.A, KeyModifiers = command };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, select);
        Assert.True(select.Handled);
        Assert.Equal(scope.Window.ViewModel.Books.Count, scope.Field<HashSet<Guid>>("_selectedBookIds").Count);
        var find = new KeyEventArgs { Key = Key.F, KeyModifiers = command, Source = scope.Get<Button>("ImportButton") };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, find);
        Assert.True(find.Handled);
        Assert.True(scope.Get<TextBox>("SearchBox").IsFocused);
        Assert.True(DesktopPlatform.IsCommand(command));
        Assert.False(DesktopPlatform.IsCommand(KeyModifiers.Alt));
        Assert.Equal(OperatingSystem.IsMacOS() ? "⌘F" : "Ctrl+F", DesktopPlatform.ShortcutLabel("Ctrl+F"));
    });
}

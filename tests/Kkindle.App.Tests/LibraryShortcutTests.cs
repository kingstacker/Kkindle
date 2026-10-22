using Avalonia.Controls;
using Avalonia.Input;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task LibraryShortcutsRespectDialogsPagesAndFocusedButtons() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        scope.Call("SelectBook", scope.Window.ViewModel.Books[0]);
        foreach (var name in new[] { "ConfirmationOverlay", "MessageOverlay", "OnboardingOverlay" })
        {
            var overlay = scope.Get<Control>(name);
            overlay.IsVisible = true;
            foreach (var key in new[] { Key.Enter, Key.Delete, Key.Escape, Key.F1 })
            {
                var args = new KeyEventArgs { Key = key };
                scope.Call("MainWindow_GlobalKeyDown", scope.Window, args);
                Assert.False(args.Handled);
            }
            overlay.IsVisible = false;
        }
        var buttonKey = new KeyEventArgs { Key = Key.Enter, Source = scope.Get<Button>("AllBooksButton") };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, buttonKey);
        Assert.False(buttonKey.Handled);
        scope.Call("ShowStage3Page", scope.Get<Grid>("SettingsPage"), null);
        var select = new KeyEventArgs { Key = Key.A, KeyModifiers = KeyModifiers.Control };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, select);
        Assert.False(select.Handled);
        scope.Call("ShowLibraryPage");
        select = new KeyEventArgs { Key = Key.A, KeyModifiers = KeyModifiers.Control };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, select);
        Assert.True(select.Handled);
        await Render();
    });
}

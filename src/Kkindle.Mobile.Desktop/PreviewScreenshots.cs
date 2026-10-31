using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Mobile;
using Kkindle.Mobile.Views;

namespace Kkindle.Mobile.Desktop;

/// <summary>
/// Dev-only screen walk: renders the phone shell to PNGs so a design change can
/// be reviewed in light, dark and black-paper reading without clicking through.
/// </summary>
internal static class PreviewScreenshots
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(900);

    public static void Schedule(string directory) =>
        Dispatcher.UIThread.Post(() => _ = RunAsync(directory), DispatcherPriority.Background);

    private static async Task RunAsync(string directory)
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        MobileShell? shell = null;
        MobilePreferences? original = null;
        try
        {
            Directory.CreateDirectory(directory);
            while (lifetime.MainWindow?.Content is not MobileShell) await Task.Delay(50);
            shell = (MobileShell)lifetime.MainWindow.Content;
            original = shell.Preferences.Current;
            await Task.Delay(Settle);

            foreach (var mode in new[] { MobileThemeMode.Light, MobileThemeMode.Dark })
            {
                shell.Preferences.Update(value => value with
                {
                    ThemeMode = mode,
                    ReaderTheme = mode == MobileThemeMode.Dark ? ReaderTheme.Ink : ReaderTheme.Classic
                });
                shell.ApplyThemeMode();
                await WalkAsync(shell, directory, mode.ToString().ToLowerInvariant());
            }
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "error.txt"), exception.ToString());
        }
        finally
        {
            if (original is not null) shell?.Preferences.Update(_ => original);
            lifetime.Shutdown();
        }
    }

    private static async Task WalkAsync(MobileShell shell, string directory, string suffix)
    {
        async Task Shot(string name)
        {
            await Task.Delay(Settle);
            var size = new PixelSize((int)shell.Bounds.Width, (int)shell.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(shell);
            using var file = File.Create(Path.Combine(directory, $"{name}-{suffix}.png"));
            bitmap.Save(file, new PngBitmapEncoderOptions());
        }

        shell.ShowLibrary();
        await Shot("library");
        shell.Preferences.Update(value => value with { ListView = true });
        shell.ShowLibrary();
        await Shot("library-list");
        shell.Preferences.Update(value => value with { ListView = false });

        shell.ShowLibrary();
        await Task.Delay(Settle);
        var page = (LibraryPage)shell.CurrentPage!;
        foreach (var book in page.VisibleBooks)
        {
            shell.OpenBook(book);
            await Shot($"detail-{book.FormatSummary.ToLowerInvariant()}");
            shell.OpenReader(book);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await Shot($"reader-{book.FormatSummary.ToLowerInvariant()}");
            var reader = (ReaderPage)shell.CurrentPage!;
            if (suffix == "light") await CheckGesturesAsync(shell, reader, directory);
            await reader.ToggleBookmarkAsync();
            await reader.TurnAsync(1);
            await Shot($"reader-turned-{book.FormatSummary.ToLowerInvariant()}");
            reader.SetChromeVisible(true);
            await Shot($"reader-chrome-{book.FormatSummary.ToLowerInvariant()}");
            reader.ShowTypography();
            await Shot($"reader-type-{book.FormatSummary.ToLowerInvariant()}");
            reader.ShowToc();
            await Shot($"reader-toc-{book.FormatSummary.ToLowerInvariant()}");
            var rows = shell.GetVisualDescendants().OfType<Button>().Count(button => button.Classes.Contains("row"));
            await File.AppendAllLinesAsync(Path.Combine(directory, "gestures.txt"), [$"toc rows realized: {rows}"]);
            shell.HideSheet();
            await Task.Delay(Settle);
            shell.GoBack();
            shell.GoBack();
            await Task.Delay(Settle);
        }

        if (suffix == "light") await CheckSettingsButtonsAsync(shell, directory);
        if (suffix == "light") await CheckPdfSheetAsync(shell, page, directory);

        // Collections: one filled folder and one empty, so both card states are reviewed.
        var modules = MobileComposition.Create(MobileApp.Services!);
        var collections = await modules.Library.Books.GetCollectionsAsync();
        var filled = collections.FirstOrDefault(item => item.Name == "预览收藏")
            ?? await modules.Library.Books.CreateCollectionAsync("预览收藏");
        if (collections.All(item => item.Name != "稍后再读"))
            await modules.Library.Books.CreateCollectionAsync("稍后再读");
        foreach (var book in page.VisibleBooks)
            await modules.Library.Books.AddBookToCollectionAsync(book.Id, filled.Id);
        shell.Preferences.Update(value => value with { CollectionsView = true });
        shell.ShowLibrary();
        await Shot("collections");
        shell.OpenCollection(filled);
        await Shot("collection");
        shell.GoBack();
        shell.Preferences.Update(value => value with { CollectionsView = false });

        shell.ShowLibrary();
        await Shot("library-after");
        shell.ShowMaterials();
        await Shot("materials");
        shell.ShowProfile();
        await Shot("profile");
    }

    /// <summary>Drives the reader's real routed pointer events, as a finger would.</summary>
    private static async Task CheckGesturesAsync(MobileShell shell, ReaderPage reader, string directory)
    {
        var results = new List<string>();
        // A synced book whose file is not downloaded opens to an error, not a surface.
        if (reader.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(control => control is NativeReaderHost or NativePdfReaderHost) is not { } surface)
        {
            await File.AppendAllLinesAsync(Path.Combine(directory, "gestures.txt"), ["SKIP book without a readable file"]);
            return;
        }
        var root = (Visual)TopLevel.GetTopLevel(shell)!;
        var (width, height) = (surface.Bounds.Width, surface.Bounds.Height);
        var middle = height / 2;

        async Task Gesture(string name, double fromX, double fromY, double toX, double toY, Func<bool> expected)
        {
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, isPrimary: true);
            var start = surface.TranslatePoint(new Point(fromX, fromY), root)!.Value;
            var end = surface.TranslatePoint(new Point(toX, toY), root)!.Value;
            var down = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
            var move = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
            var up = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
            surface.RaiseEvent(new PointerPressedEventArgs(surface, pointer, root, start, 1, down, KeyModifiers.None));
            surface.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, surface, pointer, root, end, 2, move, KeyModifiers.None));
            surface.RaiseEvent(new PointerReleasedEventArgs(surface, pointer, root, end, 3, up, KeyModifiers.None, MouseButton.Left));
            await Task.Delay(Settle);
            results.Add($"{(expected() ? "PASS" : "FAIL")} {name}: {reader.PositionText}");
        }

        var begin = reader.PositionText;
        await Gesture("tap right turns forward", width * .9, middle, width * .9, middle, () => reader.PositionText != begin);
        await Gesture("tap left turns back", width * .1, middle, width * .1, middle, () => reader.PositionText == begin);
        await Gesture("swipe left turns forward", width * .8, middle, width * .2, middle, () => reader.PositionText != begin);
        await Gesture("swipe right turns back", width * .2, middle, width * .8, middle, () => reader.PositionText == begin);
        await Gesture("vertical drag does nothing", width * .5, height * .3, width * .5, height * .7, () => reader.PositionText == begin);
        await Gesture("center tap shows chrome", width * .5, middle, width * .5, middle, () => reader.IsChromeVisible);
        await Gesture("tap hides chrome, no turn", width * .9, middle, width * .9, middle,
            () => !reader.IsChromeVisible && reader.PositionText == begin);
        await File.AppendAllLinesAsync(Path.Combine(directory, "gestures.txt"), results.Prepend($"[{begin}]"));
    }

    /// <summary>Taps the AI page's save button the way a finger would and reports what happened.</summary>
    private static async Task CheckSettingsButtonsAsync(MobileShell shell, string directory)
    {
        shell.OpenAiSettings();
        await Task.Delay(Settle);
        var page = (AiSettingsPage)shell.CurrentPage!;
        var save = page.GetVisualDescendants().OfType<Button>().First(button => button.Name == "SaveButton");
        var clicks = 0;
        save.Click += (_, _) => clicks++;
        var root = (Visual)TopLevel.GetTopLevel(shell)!;
        var center = save.TranslatePoint(new Point(save.Bounds.Width / 2, save.Bounds.Height / 2), root)!.Value;
        var hit = (root as IInputElement)?.InputHitTest(center) as Visual;
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, isPrimary: true);
        var down = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var up = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
        var target = (Avalonia.Interactivity.Interactive)(hit ?? save);
        target.RaiseEvent(new PointerPressedEventArgs(target, pointer, root, center, 1, down, KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, root, center, 2, up, KeyModifiers.None, MouseButton.Left));
        await Task.Delay(Settle);
        await File.AppendAllLinesAsync(Path.Combine(directory, "gestures.txt"),
            [$"{(clicks == 1 ? "PASS" : "FAIL")} ai save button clicks={clicks} hit={hit?.GetType().Name}:{(hit as Control)?.Name}"]);
        shell.GoBack();
        await Task.Delay(Settle);
    }

    /// <summary>With a zoomed PDF behind it, a sheet button must still receive the tap.</summary>
    private static async Task CheckPdfSheetAsync(MobileShell shell, LibraryPage library, string directory)
    {
        if (library.VisibleBooks.FirstOrDefault(book => book.FormatSummary.Contains("PDF")) is not { } pdfBook) return;
        await CheckPinchAsync(shell, pdfBook, directory);
        shell.Preferences.Update(value => value with { PdfZoom = 2 });
        shell.OpenReader(pdfBook);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var reader = (ReaderPage)shell.CurrentPage!;
        reader.ShowTypography();
        await Task.Delay(Settle);
        var button = shell.GetVisualDescendants().OfType<Button>().First(item => item.Name == "ZoomPage");
        var root = (Visual)TopLevel.GetTopLevel(shell)!;
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), root)!.Value;
        var hit = (root as IInputElement)?.InputHitTest(center) as Visual;
        var chain = string.Join(" < ", (hit?.GetSelfAndVisualAncestors() ?? []).Take(6)
            .Select(visual => $"{visual.GetType().Name}:{(visual as Control)?.Name}"));
        var underButton = hit?.GetSelfAndVisualAncestors().Contains(button) == true;
        await File.AppendAllLinesAsync(Path.Combine(directory, "gestures.txt"),
            [$"{(underButton ? "PASS" : "FAIL")} pdf sheet zoom button reachable: {chain}"]);
        shell.HideSheet();
        shell.Preferences.Update(value => value with { PdfZoom = 1 });
        await Task.Delay(Settle);
        shell.GoBack();
        await Task.Delay(Settle);
    }

    /// <summary>Two touch points moving apart must zoom a whole-page PDF in.</summary>
    private static async Task CheckPinchAsync(MobileShell shell, Book pdfBook, string directory)
    {
        shell.Preferences.Update(value => value with { PdfZoom = 1 });
        shell.OpenReader(pdfBook);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var reader = (ReaderPage)shell.CurrentPage!;
        var surface = reader.GetVisualDescendants().OfType<NativePdfReaderHost>().First();
        await Task.Delay(Settle);
        var before = surface.Zoom;
        var root = (Visual)TopLevel.GetTopLevel(shell)!;
        var center = new Point(surface.Bounds.Width / 2, surface.Bounds.Height / 2);
        Point At(double dx) => surface.TranslatePoint(new Point(center.X + dx, center.Y), root)!.Value;
        var first = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, isPrimary: true);
        var second = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, isPrimary: false);
        var down = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var move = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        var up = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
        ulong time = 1;
        surface.RaiseEvent(new PointerPressedEventArgs(surface, first, root, At(-40), time++, down, KeyModifiers.None));
        surface.RaiseEvent(new PointerPressedEventArgs(surface, second, root, At(40), time++, down, KeyModifiers.None));
        for (var step = 1; step <= 10; step++)
        {
            surface.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, surface, first, root, At(-40 - step * 8), time++, move, KeyModifiers.None));
            surface.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, surface, second, root, At(40 + step * 8), time++, move, KeyModifiers.None));
            await Task.Delay(30);
        }
        surface.RaiseEvent(new PointerReleasedEventArgs(surface, second, root, At(120), time++, up, KeyModifiers.None, MouseButton.Left));
        surface.RaiseEvent(new PointerReleasedEventArgs(surface, first, root, At(-120), time++, up, KeyModifiers.None, MouseButton.Left));
        await Task.Delay(Settle);
        var after = surface.Zoom;
        await File.AppendAllLinesAsync(Path.Combine(directory, "gestures.txt"),
            [$"{(after > before + 0.5 ? "PASS" : "FAIL")} pinch zoom {before:0.##} -> {after:0.##}, saved {shell.Preferences.Current.PdfZoom:0.##}"]);
        shell.Preferences.Update(value => value with { PdfZoom = 1 });
        shell.GoBack();
        await Task.Delay(Settle);
    }
}

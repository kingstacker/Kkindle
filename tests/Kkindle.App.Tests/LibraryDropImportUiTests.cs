using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task DroppingPdfAndTxtImportsBothAfterTxtChapterConfirmation() => Run(async () =>
    {
        await using var scope = await TestWindow.Create(new Kkindle.Core.AppSettings
        {
            UiLanguage = "zh-CN", OnboardingCompleted = true, NetworkEnabled = false,
            AutoUpdateCheckEnabled = false, AutoConnectDevice = false,
            AutoGenerateEpubAndAzw3OnImport = false, AutoDoubanMatchOnImport = false
        });
        var pdf = PdfFixture.Write(scope.Paths.Data);
        var txt = Path.Combine(scope.Paths.Data, "拖入%20测试.TXT");
        await File.WriteAllTextAsync(txt, "第一章 开始\n这是第一章正文。\n第二章 后续\n这是第二章正文。");
        using var transfer = new DataTransfer();
        var format = DataFormat.CreateStringPlatformFormat("text/uri-list");
        transfer.Add(DataTransferItem.Create(format, new Uri(pdf).AbsoluteUri + "\r\n" + new Uri(scope.Paths.Data + Path.DirectorySeparatorChar).AbsoluteUri + Uri.EscapeDataString(Path.GetFileName(txt))));

        scope.Window.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, transfer,
            scope.Window, new Point(200, 200), KeyModifiers.None));
        Assert.True(scope.Get<Border>("LibraryDropOverlay").IsVisible);
        await Render();
        Capture(scope.Window, "library-drop-formats");
        scope.Window.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer,
            scope.Window, new Point(200, 200), KeyModifiers.None));
        await Until(() => scope.Get<Grid>("ImportFormatSelectionOverlay").IsVisible);
        Assert.Equal(2, scope.Get<StackPanel>("ImportFormatSelectionList").Children.Count);
        scope.Call("ImportFormatSelectionPrimaryButton_Click", null, new RoutedEventArgs());
        await Until(() => scope.Get<Grid>("TxtImportRulesOverlay").IsVisible
            && scope.Get<ItemsControl>("TxtImportPreviewItems").Items.Count > 0);
        await Render();
        Capture(scope.Window, "library-drop-txt-preview");
        scope.Call("TxtImportConfirmButton_Click", null, new RoutedEventArgs());
        await UntilSettled(() => scope.Window.ViewModel.Books.Count == 2, "PDF and TXT drop should import both books");
        Assert.False(scope.Get<Border>("LibraryDropOverlay").IsVisible);
        await Render();
        Capture(scope.Window, "library-drop-imported");
    });
}

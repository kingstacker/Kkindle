using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Kkindle.Core;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task AboutUserGroupClickCopiesOnlyGroupNumber(string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Kkindle.App)Application.Current!).ApplyLanguage(language);
        scope.Call("ShowStage3Page", scope.Get<Grid>("SettingsPage"), null);
        scope.Call("ShowSettingsSection", "About");
        var button = scope.Get<Button>("AboutUserGroupButton");
        await Render();
        Assert.True(button.IsEffectivelyVisible);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => scope.Get<Control>("SettingsSavedCapsule").IsVisible);
        Assert.Equal("1109898894", await scope.Window.Clipboard!.TryGetTextAsync());
        Assert.Equal(language == "en-US" ? "Group number copied to clipboard." : "群号已复制到剪贴板。", scope.Get<TextBlock>("SettingsSavedCapsuleText").Text);
        await Render();
        Capture(scope.Window, $"{language}-about-user-group-copy");
    });

    [Fact]
    public Task DeletedBookNotesDisplayOriginalTitleAndCover() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var book = scope.Window.ViewModel.Books[0].Book;
        var library = scope.Field<IBookLibraryService>("_library");
        var reader = scope.Field<Kkindle.Infrastructure.ReaderDataService>("_readerData");
        await reader.SaveAnnotationAsync(new ReaderAnnotation
        {
            BookId = book.Id, BookFileId = book.Files[0].Id,
            ChapterPath = "chapter.xhtml", SelectedText = "保留的划线", Note = "保留的批注", EndOffset = 5
        });
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingMaterialsPage"), null);
        await scope.Call<Task>("RefreshReadingMaterialsAsync");
        var before = Assert.Single(scope.Window.ReadingMaterialGroups);
        var hadCover = before.HasCover;
        Assert.True(hadCover);
        Assert.False(before.HasDeletedBook);
        await library.DeleteAsync(book.Id);
        await scope.Call<Task>("RefreshReadingMaterialsAsync");
        var group = Assert.Single(scope.Window.ReadingMaterialGroups);
        Assert.Equal(book.Title, group.BookTitle);
        Assert.True(group.HasCover);
        Assert.True(group.HasDeletedBook);
        Assert.Equal("已删除", group.DeletedLabel);
        group.IsExpanded = true;
        await Render();
        Capture(scope.Window, "deleted-book-notes-retained-title-cover");
        await library.RestoreTrashItemAsync(Assert.Single(await library.GetTrashItemsAsync()).Id);
        await scope.Call<Task>("RefreshReadingMaterialsAsync");
        Assert.False(Assert.Single(scope.Window.ReadingMaterialGroups).HasDeletedBook);
    });

    [Theory]
    [InlineData("zh-CN", "ConfirmationCancelButton")]
    [InlineData("zh-CN", "ConfirmationKeepFilesButton")]
    [InlineData("zh-CN", "ConfirmationOkButton")]
    [InlineData("en-US", "ConfirmationKeepFilesButton")]
    public Task RemovingLastFormatOffersCancelKeepFilesAndTrash(string language, string action) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        ((Kkindle.App)Application.Current!).ApplyLanguage(language);
        scope.Window.Width = 1024;
        var card = scope.Window.ViewModel.Books[0];
        var file = Assert.Single(card.Book.Files);
        var library = scope.Field<IBookLibraryService>("_library");
        var path = library.GetAbsoluteFilePath(file);
        scope.Get<ListBox>("BookGrid").SelectedItem = card;

        var removal = scope.Call<Task>("DeleteFileAsync", file);
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        await Render();
        var keep = scope.Get<Button>("ConfirmationKeepFilesButton");
        var delete = scope.Get<Button>("ConfirmationOkButton");
        Assert.True(keep.IsEffectivelyVisible);
        Assert.Equal(language == "en-US" ? "Keep files" : "保留文件", keep.Content);
        Assert.Equal(language == "en-US" ? "Delete file" : "删除文件", delete.Content);
        Assert.Equal(delete.Background, keep.Background);
        Assert.Equal(delete.Foreground, keep.Foreground);
        AssertWithinWindow(keep, scope.Window);
        AssertWithinWindow(delete, scope.Window);
        Assert.Contains(language == "en-US" ? "restored" : "可恢复",
            scope.Get<TextBlock>("ConfirmationMessageText").Text!);
        Assert.False(removal.IsCompleted);
        Assert.NotNull(await library.GetBookAsync(card.Book.Id));
        Assert.True(File.Exists(path));
        Capture(scope.Window, $"{language}-last-format-removal");

        scope.Get<Button>(action).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await removal.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(scope.Get<Control>("ConfirmationOverlay").IsVisible);
        Assert.False(keep.IsVisible);
        if (action == "ConfirmationCancelButton")
        {
            Assert.NotNull(await library.GetBookAsync(card.Book.Id));
            Assert.True(File.Exists(path));
            Assert.Empty(await library.GetTrashItemsAsync());
        }
        else
        {
            Assert.Null(await library.GetBookAsync(card.Book.Id));
            Assert.DoesNotContain(scope.Window.ViewModel.Books, item => item.Book.Id == card.Book.Id);
            if (action == "ConfirmationKeepFilesButton")
            {
                Assert.True(File.Exists(path));
                Assert.Empty(await library.GetTrashItemsAsync());
            }
            else
            {
                Assert.False(File.Exists(path));
                var trash = Assert.Single(await library.GetTrashItemsAsync());
                Assert.Equal(LibraryTrashItemKind.Book, trash.Kind);
                await library.RestoreTrashItemAsync(trash.Id);
                Assert.NotNull(await library.GetBookAsync(card.Book.Id));
                Assert.True(File.Exists(path));
            }
        }
    });

    [Fact]
    public Task RemovingOneOfMultipleFormatsKeepsTheOtherFileAndBook() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var library = scope.Field<IBookLibraryService>("_library");
        var bookId = scope.Window.ViewModel.Books[0].Book.Id;
        var pdfSource = Path.Combine(scope.Paths.Data, "removal.pdf");
        await File.WriteAllBytesAsync(pdfSource, [1, 2, 3, 4]);
        var pdf = await library.AddFileToBookAsync(bookId, pdfSource);
        await scope.Call<Task>("RefreshLibraryAsync");
        var card = scope.Window.ViewModel.Books.Single(item => item.Book.Id == bookId);
        var epub = card.Book.Files.Single(file => file.Format == "epub");
        scope.Get<ListBox>("BookGrid").SelectedItem = card;

        var removal = scope.Call<Task>("DeleteFileAsync", pdf);
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        Assert.False(scope.Get<Button>("ConfirmationKeepFilesButton").IsVisible);
        Assert.Contains("PDF", scope.Get<TextBlock>("ConfirmationMessageText").Text!);
        Assert.Contains("回收站", scope.Get<TextBlock>("ConfirmationMessageText").Text!);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await removal.WaitAsync(TimeSpan.FromSeconds(10));

        var remaining = await library.GetBookAsync(bookId);
        Assert.NotNull(remaining);
        Assert.Equal(epub.Id, Assert.Single(remaining.Files).Id);
        Assert.True(File.Exists(library.GetAbsoluteFilePath(epub)));
        Assert.False(File.Exists(library.GetAbsoluteFilePath(pdf)));
        Assert.Equal(LibraryTrashItemKind.File, Assert.Single(await library.GetTrashItemsAsync()).Kind);

        scope.Get<ListBox>("BookGrid").SelectedItem = scope.Window.ViewModel.Books.Single(item => item.Book.Id == bookId);
        var lastRemoval = scope.Call<Task>("DeleteFileAsync", epub);
        await Until(() => scope.Get<Button>("ConfirmationKeepFilesButton").IsVisible);
        scope.Get<Button>("ConfirmationCancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await lastRemoval.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(await library.GetBookAsync(bookId));
        Assert.True(File.Exists(library.GetAbsoluteFilePath(epub)));
    });
}

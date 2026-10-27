using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class ReaderAiNavigationTests(SettingsUiSession session)
{
    [Fact]
    public Task TurnMarkersPreviewJumpTrackScrollingAndClearWithTheConversation() => session.Session.Dispatch(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ShowReaderAiTab");
        scope.Get<Control>("ReaderAiEmptyState").IsVisible = false;
        var rail = scope.Get<Border>("ReaderAiTurnNavigation");
        Assert.False(rail.IsVisible);
        for (var round = 1; round <= 5; round++)
        {
            scope.Window.ReaderAiMessages.Add(new ReaderAiMessageViewModel("user", $"第 {round} 个问题：这段话是什么意思？"));
            scope.Window.ReaderAiMessages.Add(new ReaderAiMessageViewModel("assistant",
                string.Join("\n\n", Enumerable.Repeat($"这是第 {round} 轮回答。结合上下文，可以这样理解这段话。", 12))));
        }
        await ReaderTests.Render();
        Assert.True(rail.IsEffectivelyVisible);
        var markers = scope.Get<StackPanel>("ReaderAiTurnMarkers").Children.OfType<Button>().ToArray();
        Assert.Equal(5, markers.Length);
        Assert.InRange(rail.Bounds.Width, 1, 26);
        var scroll = scope.Get<ScrollViewer>("ReaderAiScrollViewer");
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height * 2);
        Assert.Contains("active", markers[0].Classes);

        markers[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await ReaderTests.Render();
        var question = Assert.IsType<ReaderAiMessageViewModel>(markers[2].Tag);
        var container = scope.Get<ItemsControl>("ReaderChatMessagesPanel").ContainerFromItem(question)!;
        Assert.InRange(container.TranslatePoint(default, scroll)!.Value.Y, scroll.Padding.Top - 2, scroll.Padding.Top + 2);
        Assert.Contains("active", markers[2].Classes);
        Assert.Single(markers, marker => marker.Classes.Contains("active"));
        Assert.False(scope.Field<bool>("_readerAiFollowOutput"));

        var hover = markers[2].TranslatePoint(new Point(13, 6), scope.Window)!.Value;
        scope.Window.MouseMove(hover);
        await ReaderTests.Render();
        var preview = Assert.IsType<StackPanel>(ToolTip.GetTip(markers[2]));
        Assert.StartsWith("第 3 个问题", Assert.IsType<TextBlock>(preview.Children[0]).Text);
        ToolTip.SetIsOpen(markers[2], true);
        await ReaderTests.Render();
        var answer = scope.Window.ReaderAiMessages[5];
        answer.Update("更新后的回答仍显示在预览里。", string.Empty, true);
        await ReaderTests.Render();
        Assert.Equal(answer.Content, Assert.IsType<TextBlock>(preview.Children[1]).Text);
        var output = Environment.GetEnvironmentVariable("KKINDLE_READER_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            using var frame = scope.Window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(output, "ai-turn-navigation.png"), PngBitmapEncoderOptions.Default);
        }
        ToolTip.SetIsOpen(markers[2], false);
        scroll.ScrollToEnd();
        await ReaderTests.Render();
        Assert.Contains("active", markers[^1].Classes);
        markers[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await ReaderTests.Render();
        var offset = scroll.Offset.Y;
        scope.Call("QueueReaderAiScrollToEnd", false);
        await ReaderTests.Render();
        Assert.Equal(offset, scroll.Offset.Y);
        Assert.True(scope.Get<Button>("ReaderAiNewContentButton").IsVisible);
        for (var round = 6; round <= 35; round++)
        {
            scope.Window.ReaderAiMessages.Add(new ReaderAiMessageViewModel("user", $"第 {round} 个问题"));
            scope.Window.ReaderAiMessages.Add(new ReaderAiMessageViewModel("assistant", "简短回答。"));
        }
        await ReaderTests.Render();
        scroll.ScrollToEnd();
        await ReaderTests.Render();
        Assert.InRange(rail.Bounds.Height, 1, scroll.Bounds.Height);
        var markerScroll = rail.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.True(markerScroll.Offset.Y > 0);
        Assert.Contains("active", scope.Get<StackPanel>("ReaderAiTurnMarkers").Children.OfType<Button>().Last().Classes);
        scope.Call("ClearReaderAiCollections");
        await ReaderTests.Render();
        Assert.False(rail.IsVisible);
        Assert.Empty(scope.Get<StackPanel>("ReaderAiTurnMarkers").Children);
        return true;
    }, CancellationToken.None);
}

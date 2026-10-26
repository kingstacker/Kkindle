using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Headless;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
[Trait("Category", "Slow")]
public sealed class ReaderSelectionTtsTests(SettingsUiSession session)
{
    [Theory]
    [InlineData(false, "窗外的树影", TtsOptions.DefaultVoice)]
    [InlineData(false, "The afternoon light falls softly across the page.", TtsSelectionVoicePolicy.EnglishVoice)]
    [InlineData(true, "Select this text for a note.", TtsSelectionVoicePolicy.EnglishVoice)]
    public Task SelectionMenuReadsTheSelectedTextWithTheMatchingVoice(bool pdf, string text, string voice)
        => session.Session.Dispatch(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Set("_readerTtsEnvironmentChecked", true);
        BookCardViewModel? card = null;
        NativeReaderHost? epubHost = null;
        if (pdf)
        {
            var path = PdfFixture.Write(scope.Paths.ReaderCache);
            var library = scope.Field<IBookLibraryService>("_library");
            await library.ImportAsync([path]);
            var book = Assert.Single(await library.SearchAsync());
            card = new BookCardViewModel(book, scope.Paths.Data);
            await scope.Call<Task>("OpenPdfReaderAsync", card, book.Files[0], library.GetAbsoluteFilePath(book.Files[0]));
            await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
            var host = scope.Field<NativePdfReaderHost>("_readerActiveHost");
            var start = host.PageContent!.Text.IndexOf(text, StringComparison.Ordinal);
            Assert.True(start >= 0);
            host.SelectRange(start, start + text.Length);
        }
        else
        {
            epubHost = await ReaderAppearanceTests.LoadChapter(scope, new());
            Assert.Contains(text, epubHost.BodyText);
            scope.Set("_readerPendingSelection", text);
            scope.Call("ShowReaderSelectionPopup", null, null);
        }

        var oldService = scope.Field<TtsService>("_readerTts");
        var engine = new SelectionEngine();
        using var service = new TtsService(engine, new TtsCacheManager(Path.Combine(scope.Paths.Data, "selection-audio")),
            new SelectionPlayer(), () => scope.Call<ReaderTtsDocument?>("GetCurrentReaderTtsDocument"));
        scope.Set("_readerTts", service);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        service.StateChanged += (_, args) =>
        {
            if (args.State == TtsPlaybackState.Generating) started = true;
            if (started && args.State == TtsPlaybackState.Stopped) finished.TrySetResult();
        };
        try
        {
            await ReaderTests.Render();
            Assert.Equal(text, scope.Field<string>("_readerPendingSelection"));
            Assert.True(scope.Get<Popup>("ReaderSelectionHostPopup").IsOpen);
            var button = scope.Get<Button>("ReaderSelectionSpeakButton");
            Assert.Equal("朗读", ToolTip.GetTip(button));
            var highlightButton = scope.Get<Button>("ReaderSelectionHighlightMenuButton");
            var root = TopLevel.GetTopLevel(highlightButton)!;
            var copyButton = ((Panel)scope.Get<Border>("ReaderSelectionPopupBar").Child!).Children.OfType<Button>().First();
            var copyPoint = copyButton.TranslatePoint(new Avalonia.Point(16, 16), root)!.Value;
            root.MouseMove(copyPoint);
            await Task.Delay(ToolTip.GetShowDelay(copyButton) + 150);
            await ReaderTests.Render();
            Assert.True(ToolTip.GetIsOpen(copyButton));
            AssertToolTipOutsideToolbar(copyButton, scope.Get<Border>("ReaderSelectionPopupBar"));
            await Task.Delay(950);
            await ReaderTests.Render();
            Assert.False(ToolTip.GetIsOpen(copyButton), "Copy tooltip should dismiss while the pointer stays over it.");
            var hoverPoint = highlightButton.TranslatePoint(new Avalonia.Point(20, 16), root)!.Value;
            root.MouseMove(hoverPoint);
            scope.Call("StopReaderSelectionHighlightPointerTracking");
            // The production timer polls the native screen cursor, which is
            // separate from the headless pointer. Feed its hover samples here.
            scope.Call("UpdateReaderSelectionHighlightToolTip", true);
            Assert.False(ToolTip.GetIsOpen(highlightButton));
            await Task.Delay(ToolTip.GetShowDelay(highlightButton) + 150);
            scope.Call("UpdateReaderSelectionHighlightToolTip", true);
            await ReaderTests.Render();
            Assert.True(highlightButton.Flyout!.IsOpen);
            Assert.True(ToolTip.GetIsOpen(highlightButton), "Highlight tooltip should appear while its menu is open.");
            AssertToolTipOutsideToolbar(highlightButton, scope.Get<Border>("ReaderSelectionPopupBar"));
            await Task.Delay(950);
            scope.Call("UpdateReaderSelectionHighlightToolTip", true);
            await ReaderTests.Render();
            Assert.False(ToolTip.GetIsOpen(highlightButton), "Highlight tooltip should dismiss even with its menu open.");
            scope.Call("UpdateReaderSelectionHighlightToolTip", true);
            Assert.False(ToolTip.GetIsOpen(highlightButton), "A stationary pointer must not reopen the tooltip.");
            scope.Call("UpdateReaderSelectionHighlightToolTip", false);
            Assert.False(ToolTip.GetIsOpen(highlightButton));
            Assert.Contains("active", highlightButton.Classes);
            highlightButton.Flyout.Hide();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(text, engine.Text);
            Assert.Equal(voice, engine.Options!.Voice);
            Assert.Equal(1, engine.Count);
            Assert.Equal(TtsPlaybackState.Stopped, service.State);
            Assert.False(scope.Get<Popup>("ReaderSelectionHostPopup").IsOpen);

            await scope.Call<Task>("RefreshReaderTtsVoicesAsync");
            var choices = scope.Get<ComboBox>("ReaderTtsVoiceBox").Items.OfType<ComboBoxItem>().ToArray();
            Assert.Contains(choices, item => item.Tag as string == TtsSelectionVoicePolicy.EnglishVoice);
            Assert.Contains(choices, item => item.Tag as string == TtsOptions.DefaultVoice);
            Assert.DoesNotContain(choices, item => item.Tag as string == "en-GB-SoniaNeural");
        }
        finally
        {
            scope.Call("CancelReaderTtsContinuation");
            await service.StopAsync();
            scope.Set("_readerTts", oldService);
            await scope.Call<Task>("CloseReaderAsync");
            epubHost?.Dispose();
            card?.Dispose();
        }
        return true;
    }, CancellationToken.None);

    private static void AssertToolTipOutsideToolbar(Button button, Border toolbar)
    {
        var tip = Assert.IsType<ToolTip>(button.GetValue(Avalonia.Controls.Diagnostics.ToolTipDiagnostics.ToolTipProperty));
        var tipTop = tip.PointToScreen(default).Y;
        var tipBottom = tip.PointToScreen(new Point(0, tip.Bounds.Height)).Y;
        var barTop = toolbar.PointToScreen(default).Y;
        var barBottom = toolbar.PointToScreen(new Point(0, toolbar.Bounds.Height)).Y;
        Assert.True(tipBottom <= barTop - 4 || tipTop >= barBottom + 4,
            $"Tooltip ({tipTop}..{tipBottom}) must leave a gap outside toolbar ({barTop}..{barBottom}).");
    }

    private sealed class SelectionEngine : ITtsEngine
    {
        public string Id => TtsOptions.DefaultProvider;
        public bool IsAvailable => true;
        public string? Text { get; private set; }
        public TtsOptions? Options { get; private set; }
        public int Count { get; private set; }
        public Task<TtsAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TtsAvailability(true, "test"));
        public Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TtsVoiceInfo>>([
                new(TtsOptions.DefaultVoice, "Chinese"),
                new(TtsSelectionVoicePolicy.EnglishVoice, "American English"),
                new("en-GB-SoniaNeural", "British English")]);
        public async Task<TtsResult> SynthesizeAsync(string text, TtsOptions options, CancellationToken cancellationToken = default)
        {
            Text = text;
            Options = options;
            Count++;
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp3");
            await File.WriteAllBytesAsync(path, [1], cancellationToken);
            return TtsResult.Success(path);
        }
        public void Dispose() { }
    }

    private sealed class SelectionPlayer : ITtsAudioPlayer
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool IsPaused => false;
        public Task PlayAsync(string audioPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Pause() { }
        public void Resume() { }
        public void Stop() { }
        public void Dispose() { }
    }
}

using Kkindle.Core;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile.Views;

public sealed record NativeReaderState(
    bool Ready, bool ChromeVisible, bool IsPdf, string Title, string Chapter,
    string Position, double Progress, bool Bookmark, string? Error,
    TtsPlaybackState SpeechState, string SpeechStatus, string? SelectedText);

public sealed record NativeReaderTocItem(int Id, string Title, int Level, bool Selected);
public sealed record NativeReaderSearchItem(int Id, string Title, string Excerpt);
public sealed record NativeReaderDialog(string Kind, string Title, string Text, Guid? AnnotationId = null, string? Note = null);

/// <summary>
/// Data/actions for a native frontend around the existing rendering surface.
/// This mode has no Avalonia buttons, popups, sliders, or selection toolbar in
/// the displayed tree. The renderer still owns text hit testing and pagination.
/// </summary>
public partial class ReaderPage
{
    private bool _usesNativeFrontend;
    private bool? _nativeAppAppearanceDark;
    private int _nativeAppAppearanceRevision = -1;
    private bool _nativeChromeVisible = false;
    private string? _nativeError;
    private IReadOnlyList<SearchHit> _nativeSearchHits = [];

    public event Action<NativeReaderState>? NativeStateChanged;
    public event Action<NativeReaderDialog>? NativeDialogRequested;

    public void UseNativeFrontend()
    {
        _usesNativeFrontend = true;
        TopChrome.IsVisible = false;
        BottomChrome.IsVisible = false;
        ReaderFooter.IsVisible = false;
        LoadingPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;
        SelectionLayer.IsVisible = false;
        SpeechBar.IsVisible = false;
        SpeechDock.IsVisible = false;
        ProgressSliderGroup.SetHostVisible(false);
        NotifyNativeState();
    }

    public NativeReaderState GetNativeState() => new(
        _ready, _nativeChromeVisible, _isPdf, _book.Title,
        FooterChapter.Text ?? string.Empty, FooterPosition.Text ?? string.Empty,
        _overallProgress * 100, FindCurrentBookmark() is not null, _nativeError,
        _tts?.State ?? TtsPlaybackState.Stopped, SpeechStatusText.Text ?? string.Empty,
        _selection?.Text);

    private void NotifyNativeState()
    {
        if (_usesNativeFrontend && !_disposed) NativeStateChanged?.Invoke(GetNativeState());
    }

    public IReadOnlyList<NativeReaderTocItem> GetNativeToc()
    {
        // Match the legacy sheet: exactly one entry is current, so the native
        // list can scroll to it even when the page has no entry of its own.
        var selected = CurrentTocIndex();
        return _tocEntries.Select((item, index) =>
            new NativeReaderTocItem(index, item.Title, item.Level, index == selected)).ToArray();
    }

    public Task NativeJumpToTocAsync(int index) => index >= 0 && index < _tocEntries.Count
        ? JumpToAsync(_tocEntries[index]) : Task.CompletedTask;

    public Task NativeSeekAsync(double percent) => SeekAsync(Math.Clamp(percent, 0, 100));
    public Task NativeMoveChapterAsync(int direction) => MoveChapterAsync(Math.Sign(direction));
    public Task NativeApplySettingsAsync() => ApplyReaderSettingsAsync();

    public void NativeSetAppAppearance(bool dark, int revision)
    {
        if (revision <= _nativeAppAppearanceRevision) return;
        _nativeAppAppearanceRevision = revision;
        if (_nativeAppAppearanceDark == dark) return;
        _nativeAppAppearanceDark = dark;
        if (_started && !_disposed) RefreshReaderAppearance();
    }

    public Task NativeToggleSpeechAsync() => ToggleListeningAsync();
    public void NativePauseSpeech() => _tts?.PauseOrResume();
    public Task NativeStopSpeechAsync() => _tts?.StopAsync() ?? Task.CompletedTask;
    public string NativePageText() => CurrentPageText();
    public void NativeClearSelection() => ClearSelection();

    public async Task<IReadOnlyList<NativeReaderSearchItem>> NativeSearchAsync(string query, CancellationToken token)
    {
        var hits = await SearchBookAsync(query, new Progress<string>(), token);
        token.ThrowIfCancellationRequested();
        _nativeSearchHits = hits;
        return hits.Select((hit, index) => new NativeReaderSearchItem(index, hit.Title, hit.Excerpt)).ToArray();
    }

    public Task NativeJumpToSearchAsync(int index) => index >= 0 && index < _nativeSearchHits.Count
        ? JumpToSearchHitAsync(_nativeSearchHits[index]) : Task.CompletedTask;

    public Task NativeAnnotateAsync(string? note)
    {
        if (_selection is not { } selection || _bookFile is null)
            throw new InvalidOperationException("请先长按并选择正文。");
        var annotation = NewAnnotation(selection);
        if (_annotations.Any(item => item.ChapterPath == annotation.ChapterPath
            && annotation.StartOffset < item.EndOffset && annotation.EndOffset > item.StartOffset))
            throw new InvalidOperationException("所选文字已有批注，请点击已有划线进行编辑。");
        annotation.Note = note ?? string.Empty;
        return SaveAnnotationAsync(annotation);
    }

    public Task NativeEditAnnotationAsync(Guid id, string note)
    {
        var annotation = _annotations.Find(item => item.Id == id)
            ?? throw new InvalidOperationException("批注已不存在。");
        annotation.Note = note;
        return SaveAnnotationAsync(annotation);
    }

    public Task NativeDeleteAnnotationAsync(Guid id)
    {
        var annotation = _annotations.Find(item => item.Id == id)
            ?? throw new InvalidOperationException("批注已不存在。");
        return DeleteAnnotationAsync(annotation);
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private readonly IKindleWebFileInput? _kindleWebFileInput;
    private SendToKindleWindow? _sendToKindleWindow;

    private async void OpenSendToKindleWebButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsSendToKindleWebEnabled()) return;
        var cards = GetSelectedCards();
        var files = cards.Select(card => KindleWebFilePolicy.GetCandidates(card.Book.Files).FirstOrDefault())
            .Where(file => file is not null).Cast<BookFile>().ToArray();
        await AddLibraryFilesToKindleWebAsync(files);
        if (cards.Count > files.Length)
            _sendToKindleWindow?.ShowNotice(T("部分书籍没有可发送的格式，请先转换为 EPUB 或 PDF。"));
    }

    private async Task AddLibraryFilesToKindleWebAsync(IReadOnlyList<BookFile> files)
    {
        var window = await OpenSendToKindleWebAsync();
        if (window is null) return;
        foreach (var file in files)
        {
            try
            {
                var path = ViewModel.GetAbsoluteFilePath(file);
                if (!File.Exists(path))
                    path = await EnsureBookFileAvailableAsync(file, _lifetimeCancellation.Token) ?? path;
                window.AddFiles([path]);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { return; }
            catch (Exception exception) { window.ShowNotice(UiText.Localize(exception.Message)); }
        }
    }

    private async Task<SendToKindleWindow?> OpenSendToKindleWebAsync()
    {
        if (!IsSendToKindleWebEnabled()) return null;
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync(T("网络功能已关闭"), T("请在应用设置中允许网络功能后再使用 Send to Kindle。"));
            return null;
        }
        if (_sendToKindleWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        try
        {
            var window = new SendToKindleWindow(_paths, _kindleWebFileInput, () => _appSettings.NetworkEnabled);
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                window.Width = Math.Min(window.Width, Math.Max(window.MinWidth, screen.WorkingArea.Width / screen.Scaling - 64));
                window.Height = Math.Min(window.Height, Math.Max(window.MinHeight, screen.WorkingArea.Height / screen.Scaling - 64));
            }
            _sendToKindleWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_sendToKindleWindow, window)) _sendToKindleWindow = null;
            };
            window.Show(this);
            return window;
        }
        catch (Exception exception)
        {
            _sendToKindleWindow?.CloseForShutdown();
            _sendToKindleWindow = null;
            await ShowMessageAsync(T("无法打开 Send to Kindle"), exception.Message);
            return null;
        }
    }

    private bool IsSendToKindleWebEnabled() => SendToKindleWebEnabledCheck?.IsChecked
        ?? _appSettings.SendToKindleWebEnabled;
}

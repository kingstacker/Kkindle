using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private const int ZLibraryAdvancedUnlockClickCount = 5;
    private int _zLibraryVersionClickCount;
    private bool _zLibraryAdvancedUnlocked;
    private CancellationTokenSource? _zLibrarySearchCancellation;
    private int _zLibraryPage = 1;
    private int _zLibraryPageCount;
    private int _zLibrarySearchGeneration;
    private ZLibraryBookCardViewModel? _selectedZLibraryBook;
    private bool _zLibraryEmailSending;
    private bool _zLibraryWebSending;
    public ObservableCollection<ZLibraryBookCardViewModel> ZLibraryBooks { get; } = [];

    private void AboutVersionText_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        if (++_zLibraryVersionClickCount < ZLibraryAdvancedUnlockClickCount) return;
        _zLibraryVersionClickCount = 0;
        if (_zLibraryAdvancedUnlocked)
            LockZLibraryAdvancedOptions();
        else
            UnlockZLibraryAdvancedOptions();
        e.Handled = true;
    }

    private void UnlockZLibraryAdvancedOptions()
    {
        if (_zLibraryAdvancedUnlocked) return;
        _zLibraryAdvancedUnlocked = true;
        ZLibraryBooksButton.IsVisible = true;
        SettingsAccountExpander.IsVisible = true;
        McpZLibraryFeaturesPanel.IsVisible = true;
        PopulateZLibraryControls();
        InitializeMcpSettings();
        RefreshMcpExecutableStatus();
        ShowSettingsCapsule(T("已进入开发者模式"), 2000, success: true);
    }

    private void LockZLibraryAdvancedOptions()
    {
        _zLibraryAdvancedUnlocked = false;
        _zLibrarySearchCancellation?.Cancel();
        ++_zLibrarySearchGeneration;
        ZLibraryBooksButton.IsVisible = false;
        SettingsAccountExpander.IsVisible = false;
        McpZLibraryFeaturesPanel.IsVisible = false;
        ZLibraryPage.IsVisible = false;
        InitializeMcpSettings();
        RefreshMcpExecutableStatus();
        ShowSettingsCapsule(T("已退出开发者模式"), 2000, success: true);
    }

    private void PopulateZLibraryControls()
    {
        ZLibraryEmailBox.Text = _zLibrarySettings.Email;
        ZLibraryPasswordBox.Text = _zLibrarySettings.Password;
        ZLibraryBaseUrlBox.Text = _zLibrarySettings.BaseUrl;
        UpdateZLibraryAccountStatus();
    }

    private void RefreshLocalizedZLibraryFilterItems()
    {
        if (ZLibraryExtensionBox is null || ZLibraryLanguageBox is null) return;
        var extensionIndex = ZLibraryExtensionBox.SelectedIndex;
        var languageIndex = ZLibraryLanguageBox.SelectedIndex;
        SetComboBoxItemContent(ZLibraryExtensionBox, 0, T("全部格式"));
        SetComboBoxItemContent(ZLibraryLanguageBox, 0, T("全部语言"));
        RestoreComboBoxSelection(ZLibraryExtensionBox, extensionIndex);
        RestoreComboBoxSelection(ZLibraryLanguageBox, languageIndex);
    }

    private void ShutdownZLibrary()
    {
        _zLibrarySearchCancellation?.Cancel();
        _zLibrarySearchCancellation?.Dispose();
        _zLibrarySearchCancellation = null;
        foreach (var item in ZLibraryBooks) item.Dispose();
        ZLibraryBooks.Clear();
        _zLibraryService.Dispose();
    }
    private void UpdateZLibraryAccountStatus()
    {
        ZLibraryStatusText.Text = _zLibrarySettings.IsConfigured
            ? T("已配置账号：{0}", _zLibrarySettings.Email)
            : T("未配置账号，可搜索书籍；下载前需要登录。");
    }

    private async void ZLibraryBooksButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(ZLibraryPage);
        UpdateZLibraryAccountStatus();
        await Task.CompletedTask;
    }

    private async void ZLibrarySearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await StartZLibrarySearchAsync();
    }

    private async void ZLibrarySearchButton_Click(object? sender, RoutedEventArgs e) => await StartZLibrarySearchAsync();


    private async Task StartZLibrarySearchAsync()
    {
        var query = ZLibrarySearchBox.Text?.Trim() ?? string.Empty;
        if (!_appSettings.NetworkEnabled)
        {
            ZLibraryResultText.Text = T("网络功能已关闭，请在设置中开启。");
            await ShowMessageAsync(T("网络功能已关闭"), T("请在应用设置中允许网络功能后使用在线书库。"));
            return;
        }
        if (query.Length == 0)
        {
            ZLibraryResultText.Text = T("请输入书名或作者。");
            return;
        }
        _zLibraryPage = 1;
        await PerformZLibrarySearchAsync(query, _zLibraryPage);
    }

    private async Task PerformZLibrarySearchAsync(string query, int page)
    {
        // A new search supersedes the previous one (WinUI reference): the old
        // request is cancelled so a slow query never blocks a newer keyword.
        _zLibrarySearchCancellation?.Cancel();
        _zLibrarySearchCancellation?.Dispose();
        _zLibrarySearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var cancellation = _zLibrarySearchCancellation;
        var cancellationToken = cancellation.Token;
        var generation = ++_zLibrarySearchGeneration;

        ZLibrarySearchButton.IsEnabled = false;
        ZLibraryPrevPageButton.IsEnabled = false;
        ZLibraryNextPageButton.IsEnabled = false;
        ZLibraryResultText.Text = T("正在搜索《{0}》…", query);
        try
        {
            if (_zLibrarySettings.IsConfigured && !_zLibraryService.IsLoggedIn)
                await _zLibraryService.LoginAsync(_zLibrarySettings.Email, _zLibrarySettings.Password, _zLibrarySettings.BaseUrl, cancellationToken);
            var extension = (ZLibraryExtensionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var language = (ZLibraryLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var result = await _zLibraryService.SearchAsync(
                query,
                page,
                extensions: string.IsNullOrWhiteSpace(extension) ? null : [extension],
                languages: string.IsNullOrWhiteSpace(language) ? null : [language],
                cancellationToken: cancellationToken);
            foreach (var old in ZLibraryBooks) old.Dispose();
            ZLibraryBooks.Clear();
            CloseZLibraryDetailPanel();
            foreach (var book in result.Books)
            {
                var item = new ZLibraryBookCardViewModel(book);
                ZLibraryBooks.Add(item);
                _ = item.LoadCoverAsync(cancellationToken);
            }
            _zLibraryPage = result.Page;
            _zLibraryPageCount = result.PageCount;
            ZLibraryPageText.Text = T("第 {0} / {1} 页", _zLibraryPage, Math.Max(1, _zLibraryPageCount));
            ZLibraryResultText.Text = result.Books.Count == 0 ? T("没有找到匹配书籍。") : T("共找到 {0} 本相关书籍", result.Total);
        }
        catch (OperationCanceledException)
        {
            // A newer search superseded this one.
        }
        catch (Exception exception)
        {
            ZLibraryResultText.Text = T("搜索失败：{0}", UiText.Localize(exception.Message));
            ZLibraryPageText.Text = string.Empty;
            await ShowMessageAsync(T("搜索失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            if (generation == _zLibrarySearchGeneration)
            {
                ZLibrarySearchButton.IsEnabled = true;
                ZLibraryPrevPageButton.IsEnabled = _zLibraryPage > 1;
                ZLibraryNextPageButton.IsEnabled = _zLibraryPageCount > 0 && _zLibraryPage < _zLibraryPageCount;
            }
            if (ReferenceEquals(_zLibrarySearchCancellation, cancellation))
                _zLibrarySearchCancellation = null;
        }
    }

    private async void ZLibraryPrevPageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_zLibraryPage > 1) await PerformZLibrarySearchAsync(ZLibrarySearchBox.Text?.Trim() ?? string.Empty, _zLibraryPage - 1);
    }

    private async void ZLibraryNextPageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_zLibraryPageCount > 0 && _zLibraryPage < _zLibraryPageCount)
            await PerformZLibrarySearchAsync(ZLibrarySearchBox.Text?.Trim() ?? string.Empty, _zLibraryPage + 1);
    }

    private void ZLibraryDetailsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ZLibraryBookCardViewModel item })
        {
            _selectedZLibraryBook = item;
            ZLibraryDetailPanel.DataContext = item;
            ZLibraryDetailPanel.IsVisible = true;
        }
    }

    // Clicking a search result row opens the detail panel (WinUI reference
    // selection behaviour); the row's own buttons keep their actions.
    private void ZLibraryBookRow_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ZLibraryBookCardViewModel item }) return;
        if (IsButtonSource(e.Source)) return;
        e.Handled = true;
        _selectedZLibraryBook = item;
        ZLibraryDetailPanel.DataContext = item;
        ZLibraryDetailPanel.IsVisible = true;
    }

    private void ZLibraryDetailCloseButton_Click(object? sender, RoutedEventArgs e) => CloseZLibraryDetailPanel();

    private void CloseZLibraryDetailPanel()
    {
        _selectedZLibraryBook = null;
        ZLibraryDetailPanel.DataContext = null;
        ZLibraryDetailPanel.IsVisible = false;
    }

    private async void ZLibraryOfficialDetailButton_Click(object? sender, RoutedEventArgs e) =>
        await OpenZLibraryUrlAsync(_selectedZLibraryBook?.Book.OfficialDetailUrl, T("官网详情"));

    private async void ZLibraryReadOnlineButton_Click(object? sender, RoutedEventArgs e) =>
        await OpenZLibraryUrlAsync(_selectedZLibraryBook?.Book.ReadOnlineUrl, T("在线阅读"));

    private async void ZLibrarySendKindleWebButton_Click(object? sender, RoutedEventArgs e)
    {
        var item = _selectedZLibraryBook;
        if (item is null || item.IsDownloading || _zLibraryWebSending || _zLibraryEmailSending) return;
        _zLibraryWebSending = true;
        try
        {
            await SendZLibraryBookToKindleWebAsync(item);
        }
        finally
        {
            _zLibraryWebSending = false;
        }
    }

    private async Task SendZLibraryBookToKindleWebAsync(ZLibraryBookCardViewModel item)
    {
        if (!item.CanSendToKindleWeb)
        {
            item.SetStatus(T("该书格式不受 Kindle Web 支持。"));
            SetTaskStatus(T("该书格式不受 Kindle Web 支持。"));
            return;
        }
        if (item.Book.Size > KindleWebFilePolicy.MaximumFileBytes)
        {
            item.SetStatus(T("文件超过 200 MB，无法发送到 Kindle Web。"));
            SetTaskStatus(T("文件超过 200 MB，无法发送到 Kindle Web。"));
            await ShowMessageAsync(T("无法发送"), T("文件超过 200 MB，无法发送到 Kindle Web。"));
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            item.SetStatus(T("网络功能已关闭。"));
            await ShowMessageAsync(T("网络功能已关闭"), T("请在应用设置中允许网络功能后再发送到 Kindle Web。"));
            return;
        }
        if (!_zLibrarySettings.IsConfigured)
        {
            item.SetStatus(T("发送前请先配置 Z-Library 账号。"));
            SetTaskStatus(T("发送前请先配置 Z-Library 账号。"));
            await ShowZLibraryAccountAsync(T("发送前请先配置 Z-Library 账号。"));
            return;
        }

        if (_sendToKindleWindow is { CanAcceptOneClickSend: false } existingWindow)
        {
            existingWindow.Activate();
            existingWindow.ShowNotice(T("请先完成或清空当前 Send to Kindle Web 文件列表。"));
            return;
        }
        var sendWindow = await OpenSendToKindleWebAsync(requireEnabled: false);
        if (sendWindow is null) return;
        if (!sendWindow.CanAcceptOneClickSend)
        {
            sendWindow.Activate();
            sendWindow.ShowNotice(T("请先完成或清空当前 Send to Kindle Web 文件列表。"));
            return;
        }

        item.IsDownloading = true;
        item.SetStatus(T("正在下载，可能会消耗一次 Z-Library 下载额度…"));
        ShowTaskProgressPopup();
        TaskProgressPopupBar.IsIndeterminate = true;
        TaskProgressPopupText.Text = T("正在下载《{0}》并发送到 Kindle Web…", item.Title);
        try
        {
            if (!_zLibraryService.IsLoggedIn)
                await _zLibraryService.LoginAsync(
                    _zLibrarySettings.Email,
                    _zLibrarySettings.Password,
                    _zLibrarySettings.BaseUrl,
                    _lifetimeCancellation.Token);

            var downloadsDirectory = Path.Combine(_paths.Data, "downloads");
            var downloadedPath = await _zLibraryService.DownloadAsync(
                item.Book,
                downloadsDirectory,
                new Progress<TransferProgress>(item.SetDownloadProgress),
                _lifetimeCancellation.Token);
            _ = KindleWebFilePolicy.Inspect(downloadedPath);

            item.SetStatus(T("正在发送到 Kindle Web…"));
            TaskProgressPopupText.Text = T("正在发送《{0}》到 Kindle Web…", item.Title);
            var submitted = await sendWindow.SendOneFileImmediatelyAsync(downloadedPath, _lifetimeCancellation.Token);
            if (submitted)
            {
                item.SetStatus(T("已提交到 Kindle Web。"));
                SetTaskStatus(T("《{0}》已提交到 Kindle Web。", item.Title));
            }
            else
            {
                item.SetStatus(T("Kindle Web 发送未确认，请查看发送窗口。"));
                SetTaskStatus(T("Kindle Web 发送未确认，请查看发送窗口。"));
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            item.SetStatus(T("Kindle Web 发送已取消。"));
        }
        catch (Exception exception)
        {
            item.SetStatus(T("Kindle Web 发送失败：{0}", UiText.Localize(exception.Message)));
            SetTaskStatus(T("Kindle Web 发送失败。"));
            await ShowMessageAsync(T("发送失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            item.IsDownloading = false;
            TaskProgressPopupBar.IsIndeterminate = false;
            HideTaskProgressPopup();
        }
    }

    private async Task OpenZLibraryUrlAsync(string? value, string actionName)
    {
        if (!_appSettings.NetworkEnabled)
        {
            SetTaskStatus(T("网络功能已关闭，请在设置中启用后使用{0}。", actionName));
            await ShowMessageAsync(T("网络功能已关闭"), T("请在应用设置中允许网络功能后使用{0}。", actionName));
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            SetTaskStatus(T("无法打开{0}：这本书没有提供有效链接。", actionName));
            await ShowMessageAsync(T("无法打开{0}", actionName), T("这本书没有提供有效链接。"));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("无法打开{0}：{1}", actionName, UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法打开{0}", actionName), UiText.Localize(exception.Message));
        }
        await Task.CompletedTask;
    }

    private async void ZLibrarySendEmailButton_Click(object? sender, RoutedEventArgs e)
    {
        var item = _selectedZLibraryBook;
        if (item is null || item.IsDownloading || _zLibraryEmailSending || _zLibraryWebSending) return;
        if (!item.CanSendToEmail)
        {
            SetTaskStatus(T("该书当前不支持邮件发送，或文件不是 EPUB/PDF 格式。"));
            await ShowMessageAsync(T("无法发送"), T("该书当前不支持邮件发送，或文件不是 EPUB/PDF 格式。"));
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            SetTaskStatus(T("网络功能已关闭，请在设置中启用后再发送邮件。"));
            return;
        }
        if (!_zLibrarySettings.IsConfigured)
        {
            SetTaskStatus(T("下载并发送前请先配置 Z-Library 账号。"));
            await ShowZLibraryAccountAsync(T("下载并发送前请先配置 Z-Library 账号。"));
            return;
        }

        _kindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync(_lifetimeCancellation.Token);
        var validationError = _kindleEmailSettings.Validate();
        if (validationError is not null)
        {
            SetTaskStatus(T("请先完成 Kindle 邮箱设置：{0}", UiText.Localize(validationError)));
            KindleEmailSettingsButton_Click(null, e);
            return;
        }

        if (!await ConfirmAsync(
                T("发送到 Kindle 邮箱"),
                T("将下载《{0}》并发送到 {1}。此操作会消耗一次 Z-Library 下载额度，是否继续？", item.Title, _kindleEmailSettings.KindleEmailAddress)))
            return;

        _zLibraryEmailSending = true;
        item.IsDownloading = true;
        item.SetStatus(T("正在准备邮件…"));
        ShowTaskProgressPopup();
        TaskProgressPopupBar.IsIndeterminate = true;
        TaskProgressPopupText.Text = T("正在下载《{0}》并发送邮件…", item.Title);
        string? downloadedPath = null;
        try
        {
            if (!_zLibraryService.IsLoggedIn)
                await _zLibraryService.LoginAsync(
                    _zLibrarySettings.Email,
                    _zLibrarySettings.Password,
                    _zLibrarySettings.BaseUrl,
                    _lifetimeCancellation.Token);

            var downloadsDirectory = Path.Combine(_paths.Data, "downloads");
            downloadedPath = await _zLibraryService.DownloadAsync(
                item.Book,
                downloadsDirectory,
                new Progress<TransferProgress>(item.SetDownloadProgress),
                _lifetimeCancellation.Token);
            if (!await EnsureKindleEmailAttachmentWithinLimitAsync(item.Title, downloadedPath))
            {
                item.SetStatus(T("文件超过 50 MB，无法发送到 Kindle 邮箱"));
                return;
            }
            item.SetStatus(T("正在发送邮件…"));
            SetTaskStatus(T("正在发送《{0}》到 Kindle 邮箱…", item.Title));
            await _kindleEmailSender.SendAsync(
                _kindleEmailSettings,
                downloadedPath,
                $"Send to Kindle: {item.Title}",
                _lifetimeCancellation.Token);
            item.MarkDownloadCompleted();
            item.SetStatus(T("已发送到 Kindle 邮箱"));
            SetTaskStatus(T("《{0}》已提交到 Kindle 邮箱", item.Title));
            await ShowMessageAsync(T("发送成功"), T("邮件已发送。Amazon 完成转换后，书籍会出现在 Kindle 或 Kindle 应用中。"));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            item.SetStatus(T("邮件发送已取消"));
        }
        catch (Exception exception)
        {
            item.SetStatus(T("邮件发送失败：{0}", UiText.Localize(exception.Message)));
            SetTaskStatus(T("Kindle 邮箱发送失败"));
            await ShowMessageAsync(T("发送失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(downloadedPath))
                try { File.Delete(downloadedPath); } catch { }
            item.IsDownloading = false;
            _zLibraryEmailSending = false;
            TaskProgressPopupBar.IsIndeterminate = false;
            HideTaskProgressPopup();
        }
    }

    private async void ZLibraryDownloadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ZLibraryBookCardViewModel item } || item.IsDownloading) return;
        if (!_appSettings.NetworkEnabled)
        {
            item.SetStatus(T("网络功能已关闭"));
            await ShowMessageAsync(T("网络功能已关闭"), T("请在应用设置中允许网络功能后下载书籍。"));
            return;
        }
        if (!_zLibrarySettings.IsConfigured)
        {
            item.SetStatus(T("请先配置账号"));
            SetTaskStatus(T("请先配置 Z-Library 账号。"));
            await ShowZLibraryAccountAsync(T("请先配置 Z-Library 账号。"));
            return;
        }
        item.IsDownloading = true;
        ShowTaskProgressPopup();
        TaskProgressPopupBar.IsIndeterminate = true;
        TaskProgressPopupText.Text = T("正在下载《{0}》…", item.Title);
        try
        {
            if (!_zLibraryService.IsLoggedIn)
                await _zLibraryService.LoginAsync(_zLibrarySettings.Email, _zLibrarySettings.Password, _zLibrarySettings.BaseUrl, _lifetimeCancellation.Token);
            var downloadDirectory = Path.Combine(_paths.Data, "downloads");
            var downloaded = await _zLibraryService.DownloadAsync(item.Book, downloadDirectory, new Progress<TransferProgress>(item.SetDownloadProgress), _lifetimeCancellation.Token);
            var result = await _library.ImportAsync([downloaded], cancellationToken: _lifetimeCancellation.Token);
            if (result.FailureCount > 0) throw new IOException(result.Items.FirstOrDefault()?.Message ?? T("导入书库失败。"));
            var automaticFormats = await AutoGenerateReaderFormatsForImportsAsync(result, _lifetimeCancellation.Token);
            item.MarkDownloadCompleted();
            item.SetStatus(automaticFormats.Failures.Count == 0
                ? T("已下载并导入电脑书库")
                : T("已导入；格式补齐失败 {0} 项", automaticFormats.Failures.Count));
            await RefreshLibraryDataAsync(_lifetimeCancellation.Token);
            await RefreshCollectionsAsync();
            UpdateLibraryUi();
            try { File.Delete(downloaded); } catch { }
        }
        catch (Exception exception)
        {
            item.SetStatus(T("下载失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("下载失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            item.IsDownloading = false;
            TaskProgressPopupBar.IsIndeterminate = false;
            HideTaskProgressPopup();
        }
    }

    private async void ZLibraryAccountButton_Click(object? sender, RoutedEventArgs e) =>
        await ShowZLibraryAccountAsync();

    private Task ShowZLibraryAccountAsync(string? status = null)
    {
        OpenSettingsExpander("Library", SettingsAccountExpander);
        if (status is not null) ZLibraryAccountStatusText.Text = status;
        FocusSettingsControl(ZLibraryEmailBox);
        return Task.CompletedTask;
    }

    private void ZLibraryAccountCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        PopulateZLibraryControls();
        SettingsAccountExpander.IsExpanded = false;
        ZLibraryAccountStatusText.Text = string.Empty;
    }

    private async void ZLibraryAccountSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var settings = ZLibrarySettings.Normalize(new ZLibrarySettings
        {
            Email = ZLibraryEmailBox.Text ?? string.Empty,
            Password = ZLibraryPasswordBox.Text ?? string.Empty,
            BaseUrl = ZLibraryBaseUrlBox.Text ?? string.Empty
        });
        var validation = settings.Validate();
        if (validation is not null)
        {
            ZLibraryAccountStatusText.Text = UiText.Localize(validation);
            return;
        }
        try
        {
            if (_appSettings.NetworkEnabled)
            {
                await _zLibraryService.LoginAsync(settings.Email, settings.Password, settings.BaseUrl, _lifetimeCancellation.Token);
                settings.BaseUrl = _zLibraryService.ActiveBaseUrl;
            }
            await _zLibrarySettingsStore.SaveAsync(settings, _lifetimeCancellation.Token);
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
            _zLibrarySettings = settings;
            UpdateZLibraryAccountStatus();
            ZLibraryAccountStatusText.Text = T("账号已保存。");
            ShowSettingsSavedStatus();
        }
        catch (Exception exception) { ZLibraryAccountStatusText.Text = T("保存或验证失败：{0}", UiText.Localize(exception.Message)); }
    }

}

public sealed class ZLibraryBookCardViewModel : ObservableObject, IDisposable
{
    private static readonly HttpClient CoverClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly string[] CoverFallbackHosts = ["https://covers.z-library.sk"];
    private Bitmap? _coverImage;
    private bool _isDownloading;
    private bool _isDownloadCompleted;
    private double _downloadProgress;
    private string _statusMessage = string.Empty;

    public ZLibraryBookCardViewModel(ZLibraryBook book)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Book = book;
    }
    public ZLibraryBook Book { get; }
    public string Title => UiText.Localize(Book.Title);
    public string Authors => UiText.Localize(Book.Author);
    public string InfoLabel => Book.InfoLabel;
    public string YearLabel => Book.Year is > 0 ? Book.Year.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
    public string PublicationLabel => string.Join(" · ", new[]
    {
        Book.Publisher ?? string.Empty,
        YearLabel,
        string.IsNullOrWhiteSpace(Book.Series) ? string.Empty : UiText.Get("系列：{0}", Book.Series),
        string.IsNullOrWhiteSpace(Book.Edition) ? string.Empty : UiText.Get("版本：{0}", Book.Edition)
    }.Where(value => value.Length > 0));
    public string IdentifierLabel => string.IsNullOrWhiteSpace(Book.Identifier)
        ? string.Empty
        : $"ISBN {Book.Identifier.Replace(",", " / ", StringComparison.Ordinal)}";
    public string AvailabilityLabel => string.Join(" · ", new[]
    {
        Book.ReadOnlineAvailable ? UiText.Get("可在线阅读") : string.Empty,
        Book.KindleAvailable ? UiText.Get("支持 Kindle") : string.Empty
    }.Where(value => value.Length > 0));
    public string ExtraInfoLabel => string.Join(" · ", new[] { IdentifierLabel, AvailabilityLabel }
        .Where(value => value.Length > 0));
    public string VolumeLabel => string.IsNullOrWhiteSpace(Book.Volume) ? UiText.Get("未提供") : Book.Volume;
    public string DetailDescription
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Book.Description)) return UiText.Get("暂无简介。");
            var withoutTags = Regex.Replace(Book.Description, "<[^>]+>", " ");
            return Regex.Replace(WebUtility.HtmlDecode(withoutTags), @"\s+", " ").Trim();
        }
    }
    public string DetailMetadataLabel => string.Join(" · ", new[] { PublicationLabel, InfoLabel, IdentifierLabel }
        .Where(value => value.Length > 0));
    public bool CanOpenOfficialDetail => Uri.TryCreate(Book.OfficialDetailUrl, UriKind.Absolute, out _);
    public bool CanReadOnline => Book.ReadOnlineAvailable && Uri.TryCreate(Book.ReadOnlineUrl, UriKind.Absolute, out _);
    public bool CanSendToEmail => Book.SendToEmailAvailable
        && (Book.Extension.Equals("epub", StringComparison.OrdinalIgnoreCase)
            || Book.Extension.Equals("pdf", StringComparison.OrdinalIgnoreCase));
    public bool CanSendToKindleWeb => !IsDownloading && KindleWebFilePolicy.IsSupportedFormat(Book.Extension);
    public Bitmap? CoverImage => _coverImage;
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (value)
            {
                DownloadProgress = 0;
                if (SetProperty(ref _isDownloadCompleted, false, nameof(IsDownloadCompleted)))
                    OnPropertyChanged(nameof(IsDownloadIdle));
            }
            if (!SetProperty(ref _isDownloading, value)) return;
            OnPropertyChanged(nameof(IsNotDownloading));
            OnPropertyChanged(nameof(IsDownloadIdle));
            OnPropertyChanged(nameof(CanSendToKindleWeb));
        }
    }
    public bool IsNotDownloading => !IsDownloading;
    public bool IsDownloadCompleted => _isDownloadCompleted;
    public bool IsDownloadIdle => !IsDownloading && !IsDownloadCompleted;
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (!SetProperty(ref _downloadProgress, Math.Clamp(value, 0, 100))) return;
            OnPropertyChanged(nameof(DownloadFillWidth));
        }
    }
    public double DownloadFillWidth => DownloadProgress * 1.08;
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }
    public void SetDownloadProgress(TransferProgress progress)
    {
        DownloadProgress = progress.Percentage;
        StatusMessage = UiText.Get("正在下载 {0:0}%", progress.Percentage);
    }
    public void MarkDownloadCompleted()
    {
        DownloadProgress = 100;
        if (!SetProperty(ref _isDownloadCompleted, true, nameof(IsDownloadCompleted))) return;
        OnPropertyChanged(nameof(IsDownloadIdle));
    }
    public void SetStatus(string message) => StatusMessage = message;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Authors));
        OnPropertyChanged(nameof(InfoLabel));
        OnPropertyChanged(nameof(PublicationLabel));
        OnPropertyChanged(nameof(AvailabilityLabel));
        OnPropertyChanged(nameof(ExtraInfoLabel));
        OnPropertyChanged(nameof(VolumeLabel));
        OnPropertyChanged(nameof(DetailDescription));
        OnPropertyChanged(nameof(DetailMetadataLabel));
    }

    public void Dispose()
    {
        UiText.LanguageChanged -= OnLanguageChanged;
        _coverImage?.Dispose();
        _coverImage = null;
    }

    public async Task LoadCoverAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Book.CoverUrl) || _coverImage is not null) return;

        var attempts = new List<string> { Book.CoverUrl };
        if (Uri.TryCreate(Book.CoverUrl, UriKind.Absolute, out var coverUri))
        {
            foreach (var fallbackHost in CoverFallbackHosts)
            {
                var fallback = fallbackHost + coverUri.PathAndQuery;
                if (!string.Equals(fallback, Book.CoverUrl, StringComparison.OrdinalIgnoreCase))
                    attempts.Add(fallback);
            }
        }

        foreach (var url in attempts)
        {
            try
            {
                using var response = await CoverClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (!LooksLikeCoverImage(bytes)) continue;
                await using var stream = new MemoryStream(bytes, writable: false);
                _coverImage = new Bitmap(stream);
                OnPropertyChanged(nameof(CoverImage));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Covers are decorative and must never fail the search result.
            }
        }
    }

    private static bool LooksLikeCoverImage(byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        return (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            || (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            || (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            || (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46)
            || (bytes[0] == 0x42 && bytes[1] == 0x4D);
    }
}



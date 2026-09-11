using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private sealed record BookTranslationLanguageChoice(string Code, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record ExistingBookTranslationFile(
        Book Book,
        BookFile File,
        string Kind);

    private sealed record BookTranslationSession(
        string EpubPath,
        string Title,
        string OutputDirectory,
        BookTranslationSettings Settings,
        BookTranslationResumeMode ResumeMode,
        IReadOnlyList<ExistingBookTranslationFile> ExistingTranslationFiles,
        EpubTranslationProgressWindow ProgressWindow);

    private sealed class BookTranslationProgressReporter : IProgress<BookTranslationProgress>
    {
        private readonly EpubTranslationProgressWindow _progressWindow;
        private readonly object _gate = new();
        private TaskCompletionSource<bool>? _drained;
        private int _pending;

        public BookTranslationProgressReporter(EpubTranslationProgressWindow progressWindow)
        {
            _progressWindow = progressWindow;
        }

        public void Report(BookTranslationProgress value)
        {
            lock (_gate)
                _pending++;

            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        _progressWindow.Update(value);
                    }
                    finally
                    {
                        MarkDelivered();
                    }
                }, DispatcherPriority.Background);
            }
            catch
            {
                MarkDelivered();
                throw;
            }
        }

        public Task FlushAsync()
        {
            lock (_gate)
            {
                if (_pending == 0) return Task.CompletedTask;
                _drained ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _drained.Task;
            }
        }

        private void MarkDelivered()
        {
            TaskCompletionSource<bool>? drained = null;
            lock (_gate)
            {
                _pending--;
                if (_pending == 0)
                {
                    drained = _drained;
                    _drained = null;
                }
            }
            drained?.TrySetResult(true);
        }
    }

    private static readonly BookTranslationLanguageChoice[] BookTranslationSourceChoices =
        TranslationLanguageCatalog.All
            .Select(option => new BookTranslationLanguageChoice(option.Code, option.DisplayName))
            .ToArray();

    private static readonly BookTranslationLanguageChoice[] BookTranslationTargetChoices =
        TranslationLanguageCatalog.All
            .Where(option => !option.Code.Equals("auto", StringComparison.OrdinalIgnoreCase))
            .Select(option => new BookTranslationLanguageChoice(option.Code, option.DisplayName))
            .ToArray();

    private bool _bookTranslationInProgress;
    private bool _bookTranslationPaused;
    private bool _bookTranslationPauseRequested;
    private CancellationTokenSource? _bookTranslationCancellation;
    private EpubTranslationProgressWindow? _bookTranslationProgressWindow;
    private BookTranslationSession? _bookTranslationSession;

    private void InitializeBookTranslationControls()
    {
        TranslationSourceLanguageBox.ItemsSource = BookTranslationSourceChoices;
        TranslationTargetLanguageBox.ItemsSource = BookTranslationTargetChoices;
        PopulateBookTranslationControls();
    }

    private void PopulateBookTranslationControls()
    {
        var settings = BookTranslationSettings.Normalize(_appSettings.Translation);
        TranslationProviderBox.SelectedIndex = settings.Provider switch
        {
            BookTranslationProvider.BingFree => 1,
            BookTranslationProvider.GoogleFree => 2,
            _ => 0
        };
        TranslationSourceLanguageBox.SelectedItem = BookTranslationSourceChoices.FirstOrDefault(
            option => option.Code.Equals(settings.SourceLanguage, StringComparison.OrdinalIgnoreCase));
        TranslationTargetLanguageBox.SelectedItem = BookTranslationTargetChoices.FirstOrDefault(
            option => option.Code.Equals(settings.TargetLanguage, StringComparison.OrdinalIgnoreCase));
        TranslationAiRpmBox.Value = settings.AiRequestsPerMinute;
        TranslationAiRpmPane.IsVisible = settings.Provider == BookTranslationProvider.Ai;
        TranslationGoogleProxyBox.Text = settings.GoogleProxyAddress;
        TranslationGoogleProxyPane.IsVisible = settings.Provider == BookTranslationProvider.GoogleFree;
        TranslationTranslatedOutputCheck.IsChecked = settings.OutputMode.HasFlag(BookTranslationOutputMode.Translated);
        TranslationBilingualOutputCheck.IsChecked = settings.OutputMode.HasFlag(BookTranslationOutputMode.Bilingual);
        TranslationContextMenuEnabledCheck.IsChecked = settings.ContextMenuEnabled;
    }

    private BookTranslationSettings ReadBookTranslationSettingsFromControls()
    {
        var provider = ReadBookTranslationProviderFromControls();
        var source = TranslationSourceLanguageBox.SelectedItem is BookTranslationLanguageChoice sourceChoice
            ? sourceChoice.Code
            : _appSettings.Translation.SourceLanguage;
        var target = TranslationTargetLanguageBox.SelectedItem is BookTranslationLanguageChoice targetChoice
            ? targetChoice.Code
            : _appSettings.Translation.TargetLanguage;
        var output = BookTranslationOutputMode.None;
        if (TranslationTranslatedOutputCheck.IsChecked == true)
            output |= BookTranslationOutputMode.Translated;
        if (TranslationBilingualOutputCheck.IsChecked == true)
            output |= BookTranslationOutputMode.Bilingual;

        return BookTranslationSettings.Normalize(_appSettings.Translation with
        {
            Provider = provider,
            SourceLanguage = source,
            TargetLanguage = target,
            OutputMode = output,
            AiRequestsPerMinute = TranslationAiRpmBox.Value is { } rpm
                ? (int)rpm
                : 0,
            GoogleProxyAddress = TranslationGoogleProxyBox.Text ?? string.Empty,
            ContextMenuEnabled = TranslationContextMenuEnabledCheck.IsChecked == true
        });
    }

    private void TranslationProviderBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TranslationAiRpmPane is not null)
            TranslationAiRpmPane.IsVisible = TranslationProviderBox.SelectedIndex == 0;
        if (TranslationGoogleProxyPane is not null)
            TranslationGoogleProxyPane.IsVisible = TranslationProviderBox.SelectedIndex == 2;
        if (_bookTranslationPaused && _bookTranslationProgressWindow is { } progressWindow)
            progressWindow.SetSelectedProvider(ReadBookTranslationProviderFromControls());
        if (_suppressAppSettingsAutoSave) return;
        ScheduleAppSettingsAutoSave();
    }

    private BookTranslationProvider ReadBookTranslationProviderFromControls() =>
        TranslationProviderBox.SelectedItem is ComboBoxItem { Tag: string providerTag }
            ? providerTag switch
            {
                "bing-free" => BookTranslationProvider.BingFree,
                "google-free" => BookTranslationProvider.GoogleFree,
                _ => BookTranslationProvider.Ai
            }
            : _appSettings.Translation.Provider;

    private void TranslationSourceLanguageBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAppSettingsAutoSave) return;
        ScheduleAppSettingsAutoSave();
    }

    private void TranslationTargetLanguageBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAppSettingsAutoSave) return;
        ScheduleAppSettingsAutoSave();
    }

    private void TranslationOutputCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressAppSettingsAutoSave) return;
        if (TranslationTranslatedOutputCheck.IsChecked != true
            && TranslationBilingualOutputCheck.IsChecked != true)
        {
            TranslationTranslatedOutputCheck.IsChecked = true;
        }
        ScheduleAppSettingsAutoSave();
    }

    private void TranslationContextMenuEnabledCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressAppSettingsAutoSave) return;
        ScheduleAppSettingsAutoSave();
    }

    private async Task TranslateBookFromContextAsync(BookCardViewModel card)
    {
        if (_bookTranslationInProgress || _bookTranslationPaused) return;

        BookFile? epubFile = null;
        string? path = null;
        foreach (var candidate in card.Book.Files
                     .Where(file => file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(file => GetGeneratedTranslationKind(file.RelativePath) is null ? 0 : 1))
        {
            try
            {
                var candidatePath = _library.GetAbsoluteFilePath(candidate);
                if (File.Exists(candidatePath))
                {
                    epubFile = candidate;
                    path = candidatePath;
                    break;
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Unable to resolve EPUB path: {exception.Message}");
            }
        }

        if (epubFile is null || string.IsNullOrWhiteSpace(path))
        {
            SetTaskStatus("未找到可翻译的 EPUB 文件。 ");
            return;
        }

        await TranslateEpubFileAsync(path, card.Title, card.Book);
    }

    private async Task TranslateEpubFileAsync(
        string epubPath,
        string? bookTitle,
        Book? sourceBook = null)
    {
        if (_bookTranslationInProgress || _bookTranslationPaused) return;

        var settings = ReadBookTranslationSettingsFromControls();
        var resumeMode = BookTranslationResumeMode.Restart;
        var title = string.IsNullOrWhiteSpace(bookTitle)
            ? Path.GetFileNameWithoutExtension(epubPath)
            : bookTitle.Trim();
        var outputDirectory = GetBookTranslationOutputDirectory(epubPath);
        BookTranslationResumeInfo? resumeInfo = null;
        if (settings.OutputMode.HasFlag(BookTranslationOutputMode.Translated)
            || settings.OutputMode.HasFlag(BookTranslationOutputMode.Bilingual))
        {
            try
            {
                resumeInfo = await _epubTranslationService.FindResumeAsync(
                    epubPath,
                    outputDirectory,
                    settings,
                    _lifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Unable to inspect translation cache: {exception.Message}");
            }
        }

        if (resumeInfo is not null)
        {
            var choice = await ChooseBookTranslationResumeAsync(
                title,
                resumeInfo,
                settings,
                _lifetimeCancellation.Token);
            if (choice is null)
            {
                SetTaskStatus("已取消书籍翻译。 ");
                return;
            }
            settings = settings with { Provider = choice.Provider };
            resumeMode = choice.Mode;
        }

        var existingTranslationFiles = Array.Empty<ExistingBookTranslationFile>();
        if (settings.OutputMode.HasFlag(BookTranslationOutputMode.Translated)
            || settings.OutputMode.HasFlag(BookTranslationOutputMode.Bilingual))
        {
            try
            {
                existingTranslationFiles = (await FindExistingBookTranslationFilesAsync(
                    sourceBook,
                    title,
                    settings.OutputMode,
                    _lifetimeCancellation.Token)).ToArray();
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                SetTaskStatus($"无法检查已有翻译版本：{UiText.Localize(exception.Message)}");
                return;
            }

            if (existingTranslationFiles.Length > 0)
            {
                var existingSummary = string.Join(
                    "、",
                    existingTranslationFiles
                        .GroupBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.Count() == 1
                            ? $"{group.Key}版本"
                            : $"{group.Key}版本（{group.Count()}份）"));
                try
                {
                    var overwrite = await ConfirmAsync(
                        "已有翻译版本",
                        $"《{title}》已存在{existingSummary}。继续翻译会覆盖这些版本，并清理同类重复文件，是否继续？",
                        "覆盖并翻译",
                        _lifetimeCancellation.Token);
                    if (!overwrite)
                    {
                        SetTaskStatus("已取消书籍翻译。 ");
                        return;
                    }
                }
                catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        var requiresNetwork = settings.OutputMode.HasFlag(BookTranslationOutputMode.Translated)
            || settings.OutputMode.HasFlag(BookTranslationOutputMode.Bilingual);
        var needsProvider = resumeMode != BookTranslationResumeMode.Resume
            || resumeInfo is null
            || resumeInfo.IncompleteSegments > 0;
        if (requiresNetwork && needsProvider && !_appSettings.NetworkEnabled)
        {
            SetTaskStatus("网络访问已关闭，无法翻译书籍。 ");
            return;
        }
        if (requiresNetwork
            && needsProvider
            && settings.Provider == BookTranslationProvider.Ai
            && !_appSettings.AiEnabled)
        {
            SetTaskStatus("AI 功能已关闭，无法翻译书籍。 ");
            return;
        }

        var progressWindow = new EpubTranslationProgressWindow(
            title,
            settings.Provider,
            TranslationLanguageCatalog.Find(settings.SourceLanguage).DisplayName,
            TranslationLanguageCatalog.Find(settings.TargetLanguage).DisplayName,
            outputDirectory);
        var session = new BookTranslationSession(
            epubPath,
            title,
            outputDirectory,
            settings,
            resumeMode,
            existingTranslationFiles,
            progressWindow);
        _bookTranslationSession = session;
        _bookTranslationProgressWindow = progressWindow;
        _bookTranslationPaused = false;
        _bookTranslationPauseRequested = false;
        progressWindow.CancelRequested += (_, _) => CancelBookTranslation();
        progressWindow.PauseRequested += (_, _) => PauseBookTranslation();
        progressWindow.ResumeRequested += (_, _) => _ = ResumeBookTranslationAsync();
        progressWindow.ProviderChanged += (_, _) => SyncBookTranslationProviderFromProgressWindow(progressWindow);
        progressWindow.OpenFolderRequested += (_, _) => OpenBookTranslationOutputDirectory(outputDirectory);
        progressWindow.Show(this);

        await RunBookTranslationAsync(session);
    }

    private async Task RunBookTranslationAsync(BookTranslationSession session)
    {
        var progressWindow = session.ProgressWindow;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _bookTranslationInProgress = true;
        _bookTranslationCancellation = cancellation;
        TranslationProviderBox.IsEnabled = false;
        progressWindow.MarkRunning(session.Settings.Provider);
        var progress = new BookTranslationProgressReporter(progressWindow);
        try
        {
            var result = await _epubTranslationService.TranslateAsync(
                session.EpubPath,
                session.OutputDirectory,
                session.Settings,
                progress,
                cancellation.Token,
                session.ResumeMode);
            await progress.FlushAsync();
            // Once the EPUB has been rendered, there is no translation cache
            // left to resume. Do not treat a late cancellation during library
            // import as a pause of a now-completed translation.
            _bookTranslationPauseRequested = false;
            var translationOutputPaths = result.OutputPaths
                .Where(IsGeneratedTranslationOutputPath)
                .Where(File.Exists)
                .ToArray();
            ImportBatchResult? importResult = null;
            string? importError = null;
            if (translationOutputPaths.Length > 0)
            {
                progressWindow.MarkAddingToLibrary(translationOutputPaths.Length);
                try
                {
                    importResult = await ImportGeneratedTranslationOutputsAsync(
                        translationOutputPaths,
                        session.ExistingTranslationFiles,
                        cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    importError = UiText.Localize(exception.Message);
                }
            }

            progressWindow.MarkCompleted(result);
            if (importResult is not null)
            {
                progressWindow.MarkLibraryImportResult(importResult);
                var importedCount = importResult.Items.Count(item => item.Succeeded && item.Added);
                SetTaskStatus(importResult.FailureCount == 0
                    ? $"书籍翻译完成：{result.OutputPaths.Count} 个 EPUB 文件，已加入书库 {importedCount} 项。 "
                    : $"书籍翻译完成，已加入书库 {importedCount} 项，{importResult.FailureCount} 项加入失败。 ");
            }
            else if (importError is not null)
            {
                progressWindow.MarkLibraryImportFailed(importError);
                SetTaskStatus($"书籍翻译完成，但加入书库失败：{importError}");
            }
            else
            {
                SetTaskStatus($"书籍翻译完成：{result.OutputPaths.Count} 个 EPUB 文件。 ");
            }
            _bookTranslationSession = null;
            _bookTranslationPaused = false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var shouldPause = _bookTranslationPauseRequested
                && !_lifetimeCancellation.IsCancellationRequested;
            _bookTranslationPauseRequested = false;
            if (shouldPause)
            {
                _bookTranslationPaused = true;
                _bookTranslationSession = session with
                {
                    ResumeMode = BookTranslationResumeMode.Resume
                };
                progressWindow.MarkPaused();
                SetTaskStatus("书籍翻译已暂停，已完成内容已保存，可切换引擎后继续。 ");
            }
            else
            {
                _bookTranslationPaused = false;
                _bookTranslationSession = null;
                progressWindow.MarkCanceled();
                SetTaskStatus("书籍翻译已取消，已完成内容已保存，可下次继续。 ");
            }
        }
        catch (Exception exception)
        {
            await progress.FlushAsync();
            _bookTranslationPauseRequested = false;
            // Keep the session resumable after a provider error. The user
            // can switch engines in the progress window and continue with
            // the completed segment cache instead of starting over.
            _bookTranslationPaused = true;
            _bookTranslationSession = session with
            {
                ResumeMode = BookTranslationResumeMode.Resume
            };
            var message = UiText.Localize(exception.Message);
            progressWindow.MarkFailed(message);
            SetTaskStatus("书籍翻译失败，已保存缓存，可切换引擎后继续。 ");
        }
        finally
        {
            if (ReferenceEquals(_bookTranslationCancellation, cancellation))
                _bookTranslationCancellation = null;
            _bookTranslationInProgress = false;
            TranslationProviderBox.IsEnabled = true;
            cancellation.Dispose();
        }
    }

    private async Task ResumeBookTranslationAsync()
    {
        if (_bookTranslationInProgress
            || !_bookTranslationPaused
            || _bookTranslationSession is not { } session)
            return;

        var provider = session.ProgressWindow.SelectedProvider;
        var resumedSession = session with
        {
            Settings = BookTranslationSettings.Normalize(session.Settings with
            {
                Provider = provider,
                AiRequestsPerMinute = TranslationAiRpmBox.Value is { } rpm
                    ? (int)rpm
                    : session.Settings.AiRequestsPerMinute,
                GoogleProxyAddress = TranslationGoogleProxyBox.Text ?? session.Settings.GoogleProxyAddress
            }),
            ResumeMode = BookTranslationResumeMode.Resume
        };
        _bookTranslationSession = resumedSession;
        _bookTranslationPaused = false;
        _bookTranslationPauseRequested = false;
        SetBookTranslationProviderControls(provider);
        await RunBookTranslationAsync(resumedSession);
    }

    private void PauseBookTranslation()
    {
        if (!_bookTranslationInProgress || _bookTranslationCancellation is null) return;
        _bookTranslationPauseRequested = true;
        _bookTranslationProgressWindow?.MarkPausing();
        _bookTranslationCancellation.Cancel();
    }

    private void SyncBookTranslationProviderFromProgressWindow(
        EpubTranslationProgressWindow progressWindow)
    {
        if (!_bookTranslationPaused) return;
        SetBookTranslationProviderControls(progressWindow.SelectedProvider);
    }

    private void SetBookTranslationProviderControls(BookTranslationProvider provider)
    {
        TranslationProviderBox.SelectedIndex = provider switch
        {
            BookTranslationProvider.BingFree => 1,
            BookTranslationProvider.GoogleFree => 2,
            _ => 0
        };
    }

    private async Task<BookTranslationResumeChoice?> ChooseBookTranslationResumeAsync(
        string bookTitle,
        BookTranslationResumeInfo resumeInfo,
        BookTranslationSettings currentSettings,
        CancellationToken cancellationToken)
    {
        var dialog = new BookTranslationResumeDialog(
            bookTitle,
            resumeInfo,
            currentSettings);
        try
        {
            return await dialog.ShowAsync(this).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            dialog.Close();
            return null;
        }
    }

    private void CancelBookTranslation()
    {
        _bookTranslationPauseRequested = false;
        _bookTranslationPaused = false;
        _bookTranslationSession = null;
        _bookTranslationCancellation?.Cancel();
        if (_bookTranslationProgressWindow is { IsVisible: true } window)
            window.Close();
    }

    private void OpenBookTranslationOutputDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetTaskStatus($"无法打开输出目录：{UiText.Localize(exception.Message)}");
        }
    }

    private async Task<ImportBatchResult> ImportGeneratedTranslationOutputsAsync(
        IReadOnlyList<string> paths,
        IReadOnlyList<ExistingBookTranslationFile> existingFiles,
        CancellationToken cancellationToken)
    {
        var replacements = paths.ToDictionary(
            path => path,
            path => (IReadOnlyList<BookFile>)existingFiles
                .Where(existing => existing.Kind == GetGeneratedTranslationKind(path))
                .Select(existing => existing.File)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var result = await BookTranslationLibraryImport.ImportAsync(_library, replacements, cancellationToken);
        await ViewModel.RefreshAsync(cancellationToken);
        await RefreshLibraryMatchRecordsAsync(cancellationToken);
        await RefreshCollectionsAsync();
        UpdateLibraryUi();
        return result;
    }

    private async Task<IReadOnlyList<ExistingBookTranslationFile>> FindExistingBookTranslationFilesAsync(
        Book? sourceBook,
        string title,
        BookTranslationOutputMode outputMode,
        CancellationToken cancellationToken)
    {
        var booksById = new Dictionary<Guid, Book>();
        if (sourceBook is not null)
            booksById[sourceBook.Id] = sourceBook;

        var candidates = await _library.SearchAsync(title, cancellationToken);
        foreach (var book in candidates)
        {
            if (!SameBookMetadata(book.Title, sourceBook?.Title ?? title)
                || (sourceBook is not null && !SameBookMetadata(book.Authors, sourceBook.Authors)))
            {
                continue;
            }

            booksById[book.Id] = book;
        }

        return booksById.Values
            .SelectMany(book => book.Files.Select(file => (Book: book, File: file)))
            .Select(item =>
            {
                var kind = GetGeneratedTranslationKind(item.File.RelativePath);
                return (item.Book, item.File, Kind: kind);
            })
            .Where(item => item.Kind is not null
                && IsRequestedTranslationKind(item.Kind!, outputMode))
            .Select(item => new ExistingBookTranslationFile(item.Book, item.File, item.Kind!))
            .GroupBy(item => (item.Book.Id, item.File.Id))
            .Select(group => group.First())
            .ToArray();
    }

    private static bool SameBookMetadata(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsRequestedTranslationKind(
        string kind,
        BookTranslationOutputMode outputMode) => kind switch
        {
            "译文" => outputMode.HasFlag(BookTranslationOutputMode.Translated),
            "双语" => outputMode.HasFlag(BookTranslationOutputMode.Bilingual),
            _ => false
        };

    private static string? GetGeneratedTranslationKind(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (stem.EndsWith("_双语版", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("-双语", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_双语", StringComparison.OrdinalIgnoreCase))
        {
            return "双语";
        }

        if (stem.EndsWith("_单译版", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("-译文", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_译文", StringComparison.OrdinalIgnoreCase))
        {
            return "译文";
        }

        return null;
    }

    private static bool IsGeneratedTranslationOutputPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith("_单译版", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_双语版", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-译文", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_译文", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-双语", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_双语", StringComparison.OrdinalIgnoreCase);
    }

    private string GetBookTranslationOutputDirectory(string epubPath)
    {
        var name = Path.GetFileNameWithoutExtension(epubPath);
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeName = string.Concat(name.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character)).Trim();
        if (safeName.Length == 0) safeName = "书籍";
        if (safeName.Length > 100) safeName = safeName[..100].TrimEnd();
        return Path.Combine(_paths.Data, "translations", safeName);
    }

    private static string GetBookTranslationProviderDisplayName(BookTranslationProvider provider) => provider switch
    {
        BookTranslationProvider.BingFree => "Bing 免费翻译",
        BookTranslationProvider.GoogleFree => "Google 免费翻译",
        _ => "AI 翻译"
    };

}

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private sealed record BookTranslationLanguageChoice(string Code, string DisplayName)
    {
        public override string ToString() => DisplayName;
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
    private CancellationTokenSource? _bookTranslationCancellation;
    private EpubTranslationProgressWindow? _bookTranslationProgressWindow;

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
        TranslationOriginalOutputCheck.IsChecked = settings.OutputMode.HasFlag(BookTranslationOutputMode.Original);
        TranslationTranslatedOutputCheck.IsChecked = settings.OutputMode.HasFlag(BookTranslationOutputMode.Translated);
        TranslationBilingualOutputCheck.IsChecked = settings.OutputMode.HasFlag(BookTranslationOutputMode.Bilingual);
        TranslationContextMenuEnabledCheck.IsChecked = settings.ContextMenuEnabled;
    }

    private BookTranslationSettings ReadBookTranslationSettingsFromControls()
    {
        var provider = TranslationProviderBox.SelectedItem is ComboBoxItem { Tag: string providerTag }
            ? providerTag switch
            {
                "bing-free" => BookTranslationProvider.BingFree,
                "google-free" => BookTranslationProvider.GoogleFree,
                _ => BookTranslationProvider.Ai
            }
            : _appSettings.Translation.Provider;
        var source = TranslationSourceLanguageBox.SelectedItem is BookTranslationLanguageChoice sourceChoice
            ? sourceChoice.Code
            : _appSettings.Translation.SourceLanguage;
        var target = TranslationTargetLanguageBox.SelectedItem is BookTranslationLanguageChoice targetChoice
            ? targetChoice.Code
            : _appSettings.Translation.TargetLanguage;
        var output = BookTranslationOutputMode.None;
        if (TranslationOriginalOutputCheck.IsChecked == true)
            output |= BookTranslationOutputMode.Original;
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
            ContextMenuEnabled = TranslationContextMenuEnabledCheck.IsChecked == true
        });
    }

    private void TranslationProviderBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAppSettingsAutoSave) return;
        ScheduleAppSettingsAutoSave();
    }

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
        if (TranslationOriginalOutputCheck.IsChecked != true
            && TranslationTranslatedOutputCheck.IsChecked != true
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
        if (_bookTranslationInProgress) return;

        BookFile? epubFile = null;
        string? path = null;
        foreach (var candidate in card.Book.Files.Where(file =>
                     file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase)))
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

        await TranslateEpubFileAsync(path, card.Title);
    }

    private async Task TranslateEpubFileAsync(string epubPath, string? bookTitle)
    {
        if (_bookTranslationInProgress) return;

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
            GetBookTranslationProviderDisplayName(settings.Provider),
            TranslationLanguageCatalog.Find(settings.SourceLanguage).DisplayName,
            TranslationLanguageCatalog.Find(settings.TargetLanguage).DisplayName,
            outputDirectory);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _bookTranslationInProgress = true;
        _bookTranslationCancellation = cancellation;
        _bookTranslationProgressWindow = progressWindow;
        progressWindow.CancelRequested += (_, _) => cancellation.Cancel();
        progressWindow.OpenFolderRequested += (_, _) => OpenBookTranslationOutputDirectory(outputDirectory);
        progressWindow.Show(this);

        var progress = new Progress<BookTranslationProgress>(progressWindow.Update);
        try
        {
            var result = await _epubTranslationService.TranslateAsync(
                epubPath,
                outputDirectory,
                settings,
                progress,
                cancellation.Token,
                resumeMode);
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
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            progressWindow.MarkCanceled();
            SetTaskStatus("书籍翻译已取消，已完成内容已保存，可下次继续。 ");
        }
        catch (Exception exception)
        {
            var message = UiText.Localize(exception.Message);
            progressWindow.MarkFailed(message);
            SetTaskStatus("书籍翻译失败。 ");
        }
        finally
        {
            if (ReferenceEquals(_bookTranslationCancellation, cancellation))
                _bookTranslationCancellation = null;
            _bookTranslationInProgress = false;
            cancellation.Dispose();
        }
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
        CancellationToken cancellationToken)
    {
        var result = await ViewModel.ImportAsync(
            paths,
            cancellationToken: cancellationToken,
            // A translated and a bilingual output share the source metadata.
            // Add both as formats automatically so the default post-processing
            // does not stop for a duplicate-title dialog.
            conflictResolver: static _ =>
                Task.FromResult(ImportConflictResolution.AddAsFormat));
        await RefreshLibraryMatchRecordsAsync(cancellationToken);
        await RefreshCollectionsAsync();
        UpdateLibraryUi();
        return result;
    }

    private static bool IsGeneratedTranslationOutputPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith("-译文", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-双语", StringComparison.OrdinalIgnoreCase);
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

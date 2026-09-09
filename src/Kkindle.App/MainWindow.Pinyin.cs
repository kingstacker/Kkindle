using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private bool _pinyinGenerationInProgress;
    private CancellationTokenSource? _pinyinGenerationCancellation;
    private PinyinBookProgressWindow? _pinyinProgressWindow;

    private async Task GeneratePinyinBookAsync(BookCardViewModel card)
    {
        if (_pinyinGenerationInProgress)
        {
            await ShowMessageAsync(T("书籍注音"), T("已有一本书正在进行书籍注音，请稍候。"));
            return;
        }

        if (_conversionInProgress || _automaticReaderFormatGenerationInProgress)
        {
            await ShowMessageAsync(T("书籍注音"), T("已有一本书正在转换，请稍候。"));
            return;
        }

        if (FindPinyinBookFile(card.Book.Files) is not null)
        {
            await ShowMessageAsync(T("书籍注音"), T("这本书已经有书籍注音版。"));
            return;
        }

        var sourceFile = SelectPinyinSourceFile(card.Book.Files);
        if (sourceFile is null)
        {
            await ShowMessageAsync(
                T("书籍注音"),
                T("需要 EPUB、AZW3、MOBI 或 PDF 作为书籍注音来源。"));
            return;
        }

        var sourcePath = _library.GetAbsoluteFilePath(sourceFile);
        if (!File.Exists(sourcePath))
        {
            SetTaskStatus(T("找不到书籍注音来源：{0}", sourceFile.RelativePath));
            return;
        }

        var outputDirectory = GetPinyinOutputDirectory(card.Title);
        var outputPath = Path.Combine(
            outputDirectory,
            KindleTransferPolicy.CreateSafeFileName($"{card.Title}-拼音版", ".epub"));
        var pinyinLocalOnly = PinyinLocalOnlyCheck?.IsChecked
            ?? _appSettings.PinyinLocalOnly;
        AiConnectionSettings? aiSettings = null;
        if (!pinyinLocalOnly && _appSettings.NetworkEnabled && _appSettings.AiEnabled)
        {
            try
            {
                var loadedAiSettings = await _aiSettingsStore.LoadAsync(_lifetimeCancellation.Token);
                if (loadedAiSettings.IsConfigured)
                    aiSettings = loadedAiSettings;
            }
            catch
            {
                // Local pinyin remains available when the AI settings cannot be read.
            }
        }

        var aiReviewEnabled = !pinyinLocalOnly && aiSettings is not null;
        var pinyinOptions = new PinyinBookOptions { EnableAiReview = aiReviewEnabled };
        var pinyinService = new PinyinBookService(_formatConverter, _aiChatClient, aiSettings);
        var resumeMode = PinyinBookResumeMode.Restart;
        PinyinBookResumeInfo? resumeInfo = null;
        try
        {
            resumeInfo = await pinyinService.FindResumeAsync(
                sourcePath,
                outputPath,
                pinyinOptions,
                _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to inspect pinyin cache: {exception.Message}");
        }

        if (resumeInfo is not null)
        {
            var choice = await ChoosePinyinResumeAsync(
                card.Title,
                resumeInfo,
                pinyinOptions,
                _lifetimeCancellation.Token);
            if (choice is null)
            {
                SetTaskStatus("已取消书籍注音。 ");
                return;
            }
            resumeMode = choice.Mode;
        }

        var progressWindow = new PinyinBookProgressWindow(card.Title, aiReviewEnabled);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _pinyinGenerationInProgress = true;
        _pinyinGenerationCancellation = cancellation;
        _pinyinProgressWindow = progressWindow;
        progressWindow.CancelRequested += (_, _) => cancellation.Cancel();
        progressWindow.Show(this);

        var imported = false;
        var keepGeneratedOutput = false;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var progress = new Progress<PinyinBookProgress>(progressWindow.Update);
            var result = await pinyinService.GenerateAsync(
                sourcePath,
                outputPath,
                pinyinOptions,
                progress,
                cancellation.Token,
                resumeMode);

            if (result.AnnotatedCharacterCount == 0)
            {
                progressWindow.MarkFailed(T("没有发现可注音的中文正文。"));
                SetTaskStatus(T("没有发现可注音的中文正文。"));
                return;
            }

            if (result.FailedSegmentCount > 0 || result.AiReviewError is not null)
            {
                var reason = result.AiReviewError
                    ?? $"仍有 {result.FailedSegmentCount:N0} 段未完成 AI 复核";
                progressWindow.MarkFailed($"书籍注音未完全完成：{reason}");
                SetTaskStatus("书籍注音未完全完成，缓存已保存，下次可继续。 ");
                return;
            }

            progressWindow.MarkAddingToLibrary();
            try
            {
                await _library.AddFileToBookAsync(
                    card.Book.Id,
                    outputPath,
                    cancellation.Token);
                await RefreshLibraryAsync();
                imported = true;
                progressWindow.MarkCompleted(result);
                progressWindow.MarkLibraryAdded();
                SetTaskStatus("书籍注音生成完成，已加入书库。 ");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                keepGeneratedOutput = true;
                throw;
            }
            catch (Exception exception)
            {
                keepGeneratedOutput = true;
                var message = UiText.Localize(exception.Message);
                progressWindow.MarkCompleted(result);
                progressWindow.MarkLibraryImportFailed(message);
                SetTaskStatus($"书籍注音完成，但加入书库失败：{message}");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            progressWindow.MarkCanceled();
            SetTaskStatus("书籍注音已取消，已完成内容已保存，可下次继续。 ");
        }
        catch (Exception exception)
        {
            var message = UiText.Localize(exception.Message);
            progressWindow.MarkFailed(message);
            SetTaskStatus("书籍注音失败，缓存已保存。 ");
        }
        finally
        {
            if (imported)
                TryDeletePinyinArtifacts(outputPath, outputDirectory, removeDirectory: true);
            else if (!keepGeneratedOutput)
                TryDeletePinyinArtifacts(outputPath, outputDirectory, removeDirectory: false);

            if (ReferenceEquals(_pinyinGenerationCancellation, cancellation))
                _pinyinGenerationCancellation = null;
            if (ReferenceEquals(_pinyinProgressWindow, progressWindow))
                _pinyinProgressWindow = null;
            _pinyinGenerationInProgress = false;
            cancellation.Dispose();
        }
    }

    private async Task<PinyinBookResumeChoice?> ChoosePinyinResumeAsync(
        string bookTitle,
        PinyinBookResumeInfo resumeInfo,
        PinyinBookOptions currentOptions,
        CancellationToken cancellationToken)
    {
        var dialog = new PinyinBookResumeDialog(bookTitle, resumeInfo, currentOptions);
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

    private void CancelPinyinGeneration()
    {
        _pinyinGenerationCancellation?.Cancel();
        if (_pinyinProgressWindow is { IsVisible: true } window)
            window.Close();
    }

    private static BookFile? FindPinyinBookFile(IEnumerable<BookFile>? files) =>
        files?.FirstOrDefault(PinyinBookPolicy.IsGeneratedPinyinVersion);

    private static BookFile? SelectPinyinSourceFile(IEnumerable<BookFile>? files)
    {
        if (files is null) return null;

        var supported = files
            .Where(file => BookFormatConversionPolicy.IsCalibreInputFormat(file.Format))
            .Where(file => !PinyinBookPolicy.IsGeneratedPinyinVersion(file))
            .ToArray();
        return supported.FirstOrDefault(file =>
                   BookFormatConversionPolicy.Normalize(file.Format) == "epub")
            ?? supported.FirstOrDefault(file =>
                BookFormatConversionPolicy.Normalize(file.Format) is "azw3" or "mobi" or "pdf")
            ?? supported.FirstOrDefault();
    }

    private string GetPinyinOutputDirectory(string bookTitle)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeName = string.Concat(bookTitle.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character)).Trim();
        if (safeName.Length == 0) safeName = "书籍";
        if (safeName.Length > 100) safeName = safeName[..100].TrimEnd();
        return Path.Combine(_paths.Data, "pinyin", safeName);
    }

    private static void TryDeletePinyinArtifacts(
        string outputPath,
        string directory,
        bool removeDirectory)
    {
        try
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (removeDirectory
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

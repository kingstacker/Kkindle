using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private async Task InitializeReaderAiAsync(CancellationToken cancellationToken)
    {
        try
        {
            _readerAiSettings = await _aiSettingsStore.LoadAsync(cancellationToken);
            ApplyReaderAiSettingsToControls();
            _ = RefreshReaderAiModelSelectorAsync(cancellationToken);
            ReaderAiStatusText.Text = _appSettings.AiEnabled
                ? _readerAiSettings.IsConfigured
                    ? "AI 已就绪；回答只会使用当前书籍的本地文本片段。"
                    : "AI 尚未配置，请打开设置填写 API Key。"
                : "AI 已在应用设置中关闭。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderAiStatusText.Text = $"读取 AI 设置失败：{exception.Message}";
        }
    }

    private void ApplyReaderAiSettingsToControls()
    {
        _suppressAiProviderChange = true;
        _suppressAiModelChange = true;
        _suppressAiReasoningDepthChange = true;
        try
        {
            SelectReaderAiProvider(_readerAiSettings.Provider);
            ReaderAiBaseUrlBox.Text = _readerAiSettings.BaseUrl;
            ReaderAiModelBox.Text = _readerAiSettings.Model;
            ReaderAiApiKeyBox.Text = _readerAiSettings.ApiKey;

            var modelOptions = AiConnectionSettings.GetModelOptions(
                _readerAiSettings.Provider,
                _readerAiSettings.Model);
            _readerAiAvailableModels = modelOptions;
            ReaderAiModelSelectorBox.ItemsSource = modelOptions
                .Prepend(_readerAiSettings.Model)
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(model => new ComboBoxItem { Content = model, Tag = model })
                .ToArray();
            ReaderAiModelSelectorBox.SelectedItem = ReaderAiModelSelectorBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    _readerAiSettings.Model,
                    StringComparison.OrdinalIgnoreCase));

            UpdateReaderAiReasoningDepthSelector();
            ReaderAiProviderText.Text = $"{_readerAiSettings.ProviderDisplayName} · "
                + (_readerAiSettings.IsConfigured ? _readerAiSettings.Model : "未配置");
        }
        finally
        {
            _suppressAiProviderChange = false;
            _suppressAiModelChange = false;
            _suppressAiReasoningDepthChange = false;
        }
    }

    private void UpdateReaderAiReasoningDepthSelector()
    {
        if (ReaderAiReasoningDepthBox is null) return;

        var options = _readerAiSettings.Provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase)
            ? new[]
            {
                ("auto", "自动"),
                ("high", "深入"),
                ("max", "极致")
            }
            : new[]
            {
                ("auto", "自动"),
                ("low", "快速"),
                ("medium", "平衡"),
                ("high", "深入")
            };
        var selectedDepth = options.Any(option => option.Item1.Equals(
                _readerAiReasoningDepth,
                StringComparison.OrdinalIgnoreCase))
            ? _readerAiReasoningDepth
            : "auto";
        ReaderAiReasoningDepthBox.ItemsSource = options
            .Select(option => new ComboBoxItem
            {
                Content = option.Item2,
                Tag = option.Item1
            })
            .ToArray();
        ReaderAiReasoningDepthBox.SelectedItem = ReaderAiReasoningDepthBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                selectedDepth,
                StringComparison.OrdinalIgnoreCase));
        _readerAiReasoningDepth = selectedDepth;
    }

    private void SelectReaderAiProvider(string provider)
    {
        var normalized = provider.Trim().ToLowerInvariant();
        var item = ReaderAiProviderBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, normalized, StringComparison.OrdinalIgnoreCase));
        ReaderAiProviderBox.SelectedItem = item ?? ReaderAiProviderBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private string GetSelectedReaderAiProvider() =>
        (ReaderAiProviderBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "deepseek";

    private void ReaderAiTabButton_Click(object? sender, RoutedEventArgs e) => ShowReaderAiTab();

    private void ReaderNotesTabButton_Click(object? sender, RoutedEventArgs e) => ShowReaderNotesTab();

    private void ShowReaderAiTab()
    {
        ReaderAiView.IsVisible = true;
        ReaderNotesView.IsVisible = false;
        ReaderAiComposer.IsVisible = true;
        ReaderAiSendBar.IsVisible = true;
        ReaderNotesExportBar.IsVisible = false;
        SetReaderAssistantTabState(ReaderAiTabButton, selected: true);
        SetReaderAssistantTabState(ReaderNotesTabButton, selected: false);
    }

    private void ShowReaderNotesTab()
    {
        if (!_readerZenMode)
        {
            ReaderAssistantPanel.IsVisible = true;
            ReaderRoot.ColumnDefinitions[2].Width = new GridLength(360);
        }
        ReaderAiView.IsVisible = false;
        ReaderNotesView.IsVisible = true;
        ReaderAiComposer.IsVisible = false;
        ReaderAiSendBar.IsVisible = false;
        ReaderNotesExportBar.IsVisible = true;
        SetReaderAssistantTabState(ReaderAiTabButton, selected: false);
        SetReaderAssistantTabState(ReaderNotesTabButton, selected: true);
    }

    private static void SetReaderAssistantTabState(Button button, bool selected)
    {
        button.Classes.Set("active", selected);
    }

    private void ReaderAiSettingsOpenButton_Click(object? sender, RoutedEventArgs e)
        => ReaderAiSettingsButton_Click(sender, e);

    private void ReaderAiSettingsCancelButton_Click(object? sender, RoutedEventArgs e) => ShowReaderAiTab();

    private async void ReaderAiSettingsSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var provider = GetSelectedReaderAiProvider();
        var baseUrl = ReaderAiBaseUrlBox.Text?.Trim() ?? string.Empty;
        var model = ReaderAiModelBox.Text?.Trim() ?? string.Empty;
        var apiKey = ReaderAiApiKeyBox.Text?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
            || model.Length == 0)
        {
            ReaderAiSettingsStatusText.Text = "请填写有效的 HTTP Base URL 和模型名称。";
            return;
        }

        try
        {
            _readerAiSettings = new AiConnectionSettings
            {
                Provider = provider,
                BaseUrl = baseUrl.TrimEnd('/'),
                Model = AiConnectionSettings.NormalizeModel(provider, model),
                ApiKey = apiKey
            };
            await _aiSettingsStore.SaveAsync(_readerAiSettings, ReaderToken);
            ApplyReaderAiSettingsToControls();
            ReaderAiSettingsStatusText.Text = "AI 设置已保存。";
            ShowReaderAiTab();
            ReaderAiStatusText.Text = _readerAiSettings.IsConfigured
                ? "AI 已就绪；回答只会使用当前书籍的本地文本片段。"
                : "设置已保存，但还缺少 API Key。";
        }
        catch (Exception exception)
        {
            ReaderAiSettingsStatusText.Text = $"保存失败：{exception.Message}";
        }
    }

    private void ReaderAiProviderBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAiProviderChange
            || sender is not ComboBox providerBox
            || ReaderAiBaseUrlBox is null
            || ReaderAiModelBox is null
            || ReaderAiModelSelectorBox is null) return;
        var provider = (providerBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "deepseek";
        var defaults = AiConnectionSettings.GetDefaults(provider);
        ReaderAiBaseUrlBox.Text = defaults.BaseUrl;
        ReaderAiModelBox.Text = defaults.Model;
        var models = AiConnectionSettings.GetModelOptions(provider, defaults.Model);
        _readerAiAvailableModels = models;
        ReaderAiModelSelectorBox.ItemsSource = models
            .Prepend(defaults.Model)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(model => new ComboBoxItem { Content = model, Tag = model })
            .ToArray();
        ReaderAiModelSelectorBox.SelectedItem = ReaderAiModelSelectorBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                defaults.Model,
                StringComparison.OrdinalIgnoreCase));
    }

    private void ReaderAiModelSelectorBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAiModelChange
            || ReaderAiModelSelectorBox is null
            || ReaderAiQuestionBox is null
            || ReaderAiModelSelectorBox.SelectedItem is not ComboBoxItem { Tag: string model }) return;
        ReaderAiQuestionBox.Focus();
        _readerAiSettings.Model = model;
    }

    private void ReaderAiReasoningDepthBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAiReasoningDepthChange
            || ReaderAiReasoningDepthBox.SelectedItem is not ComboBoxItem { Tag: string depth }) return;
        _readerAiReasoningDepth = depth;
    }

    private async void ReaderAiSendButton_Click(object? sender, RoutedEventArgs e)
    {
        var question = ReaderAiQuestionBox.Text?.Trim() ?? string.Empty;
        if (question.Length == 0) return;
        ReaderAiQuestionBox.Text = string.Empty;
        await SendReaderAiQuestionAsync(question);
    }

    private async void ReaderAiQuestionBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (e.KeyModifiers & KeyModifiers.Control) == 0) return;
        e.Handled = true;
        var question = ReaderAiQuestionBox.Text?.Trim() ?? string.Empty;
        if (question.Length == 0) return;
        ReaderAiQuestionBox.Text = string.Empty;
        await SendReaderAiQuestionAsync(question);
    }

    private async void ReaderAiSummarizeChapterButton_Click(object? sender, RoutedEventArgs e)
        => await SendReaderAiQuestionAsync($"请用清晰的中文总结当前章节（{GetReaderChapterLabel()}），列出核心观点、关键人物或概念，以及值得回看的段落。");

    private async void ReaderAiExplainSelectionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_readerPendingSelection))
        {
            ReaderAiStatusText.Text = "请先在正文中选择一段文字。";
            return;
        }
        await SendReaderAiQuestionAsync($"请解释下面这段文字的含义、上下文和可能的隐含前提，并用一个简单例子帮助理解：\n\n{_readerPendingSelection}");
    }

    private async void ReaderAiSummarizeBookButton_Click(object? sender, RoutedEventArgs e)
        => await SendReaderAiQuestionAsync("请概览这本书的主题、结构、主要论点和适合继续阅读的方向；如果上下文不足，请明确说明。");

    private void ReaderAiClearButton_Click(object? sender, RoutedEventArgs e)
    {
        _readerAiConversation.Clear();
        ReaderAiMessages.Clear();
        ReaderAiSources.Clear();
        ReaderAiSourcesPanel.IsVisible = false;
        ReaderAiEmptyState.IsVisible = true;
        ReaderAiStatusText.Text = "对话已清空。";
    }

    private async Task SendReaderAiQuestionAsync(string question)
    {
        if (_readerAiBusy) return;
        if (!_appSettings.AiEnabled)
        {
            ReaderAiStatusText.Text = "AI 已在应用设置中关闭。";
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            ReaderAiStatusText.Text = "网络访问已关闭，无法调用 AI 服务。";
            return;
        }
        if (!_readerAiSettings.IsConfigured)
        {
            ReaderAiStatusText.Text = "请先到设置面板的 AI 助手设置中填写 Base URL、模型和 API Key。";
            return;
        }

        _readerAiBusy = true;
        SetReaderAiBusyState(true);
        _readerAiCancellation?.Cancel();
        _readerAiCancellation?.Dispose();
        var aiCancellation = CancellationTokenSource.CreateLinkedTokenSource(ReaderToken);
        _readerAiCancellation = aiCancellation;
        var token = aiCancellation.Token;
        var userMessage = new ReaderAiMessageViewModel("user", question);
        var assistantMessage = new ReaderAiMessageViewModel("assistant");
        ReaderAiMessages.Add(userMessage);
        ReaderAiMessages.Add(assistantMessage);
        ReaderAiEmptyState.IsVisible = false;
        ReaderAiStatusText.Text = "正在整理本地片段并请求 AI…";
        ReaderAiSources.Clear();
        ReaderAiSourcesPanel.IsVisible = false;

        try
        {
            var context = await BuildReaderAiContextAsync(question, token);
            foreach (var source in context.Sources) ReaderAiSources.Add(source);
            ReaderAiSourcesPanel.IsVisible = ReaderAiSources.Count > 0;
            var instructions = "你是 Kkindle 内置的 Kreader AI 助手。只根据用户问题和提供的书籍片段回答；不要假装读过未提供的内容。回答使用中文，简洁但有结构，必要时指出证据来自哪一章或哪一页。";
            var prompt = $"用户问题：\n{question}\n\n书籍片段：\n{context.Text}";
            var answer = new StringBuilder();
            var reasoning = new StringBuilder();
            await foreach (var chunk in _aiChatClient.StreamAsync(
                _readerAiSettings,
                instructions,
                prompt,
                _readerAiConversation,
                _readerAiReasoningDepth,
                token))
            {
                answer.Append(chunk.Text);
                reasoning.Append(chunk.Reasoning);
                assistantMessage.Update(answer.ToString(), reasoning.ToString(), isStreaming: true);
            }

            var finalAnswer = answer.ToString().Trim();
            if (finalAnswer.Length == 0) finalAnswer = "AI 没有返回可显示的正文。";
            assistantMessage.Update(finalAnswer, reasoning.ToString(), isStreaming: false);
            _readerAiConversation.Add(new AiConversationTurn("user", question));
            _readerAiConversation.Add(new AiConversationTurn("assistant", finalAnswer));
            ReaderAiStatusText.Text = $"已完成 · {DateTime.Now:HH:mm}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            assistantMessage.Update("请求已取消。", string.Empty, isStreaming: false);
            ReaderAiStatusText.Text = "AI 请求已取消。";
        }
        catch (Exception exception)
        {
            assistantMessage.Update($"请求失败：{exception.Message}", string.Empty, isStreaming: false);
            ReaderAiStatusText.Text = "AI 请求失败，请检查服务地址和 API Key。";
        }
        finally
        {
            SetReaderAiBusyState(false);
            _readerAiBusy = false;
            if (ReferenceEquals(_readerAiCancellation, aiCancellation))
                _readerAiCancellation = null;
            aiCancellation.Dispose();
        }
    }

    private void SetReaderAiBusyState(bool busy)
    {
        if (ReaderAiSendButton is not null)
            ReaderAiSendButton.IsEnabled = !busy;
        if (ReaderAiReasoningDepthBox is not null)
            ReaderAiReasoningDepthBox.IsEnabled = !busy;
        if (ReaderAiModelSelectorBox is not null)
            ReaderAiModelSelectorBox.IsEnabled = !busy;
        if (ReaderAiQuestionBox is not null)
            ReaderAiQuestionBox.IsEnabled = !busy;
    }

    // Refreshes the model list from the API when it is reachable, keeping the
    // provider-specific fallback list otherwise. Mirrors the WinUI reference.
    private async Task RefreshReaderAiModelSelectorAsync(CancellationToken sessionCancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(_readerAiSettings.BaseUrl, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme is not ("http" or "https")
                || string.IsNullOrWhiteSpace(_readerAiSettings.ApiKey))
                return;
            using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellationToken);
            refreshCancellation.CancelAfter(TimeSpan.FromSeconds(10));
            var models = await _aiChatClient.ListModelsAsync(
                _readerAiSettings,
                refreshCancellation.Token);
            if (refreshCancellation.IsCancellationRequested || models.Count == 0) return;

            if (_readerAiSettings.Provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase)
                && !models.Contains(_readerAiSettings.Model, StringComparer.OrdinalIgnoreCase))
            {
                _readerAiSettings.Model = models[0];
                await _aiSettingsStore.SaveAsync(_readerAiSettings, CancellationToken.None);
            }

            _readerAiAvailableModels = models;
            ApplyReaderAiSettingsToControls();
        }
        catch (OperationCanceledException) when (sessionCancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Keep the provider-specific fallback list when model discovery is
            // unavailable (offline network, incompatible custom endpoint, etc.).
        }
    }

    private async Task<ReaderAiContext> BuildReaderAiContextAsync(string question, CancellationToken cancellationToken)
    {
        var sources = new List<ReaderAiSourceViewModel>();
        var chunks = new List<BookContentChunk>();
        var text = new StringBuilder();
        if (_readerIsPdf)
        {
            var current = _readerPdfPages.FirstOrDefault(page => page.PageNumber == _readerPdfPage);
            if (current is not null)
            {
                sources.Add(new ReaderAiSourceViewModel(current));
                text.AppendLine($"[第 {current.PageNumber} 页]\n{LimitReaderContext(current.Text)}");
            }
            if (text.Length == 0)
                text.AppendLine("（当前 PDF 页面没有可提取的文本。）");
            return new ReaderAiContext(text.ToString(), sources);
        }

        if (_readerBookCard is null || _readerBookFile is null || _readerDocument is null)
            return new ReaderAiContext("（当前没有可用的书籍文本。）", sources);

        await _bookContent.EnsureIndexedAsync(_readerBookCard.Book, _readerBookFile, _readerDocument, cancellationToken);
        chunks.AddRange(await _readerData.SearchBookAsync(
            _readerBookCard.Book.Id,
            question,
            8,
            cancellationToken));
        if (chunks.Count == 0)
        {
            var overview = await _readerData.GetBookOverviewChunksAsync(
                _readerBookCard.Book.Id,
                8,
                cancellationToken);
            chunks.AddRange(overview
                .Where(chunk => chunk.ChapterIndex == _readerChapterIndex)
                .Concat(overview.Where(chunk => chunk.ChapterIndex != _readerChapterIndex))
                .Take(4));
        }
        foreach (var chunk in chunks.DistinctBy(chunk => chunk.Id).Take(8))
        {
            sources.Add(new ReaderAiSourceViewModel(chunk));
            text.AppendLine($"[{chunk.ChapterTitle}]\n{LimitReaderContext(chunk.Content)}");
        }
        if (text.Length == 0) text.AppendLine("（本地索引中没有找到相关片段。）");
        return new ReaderAiContext(text.ToString(), sources);
    }

    private void ReaderAiSourceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReaderAiSourceViewModel source }) return;
        _ = ObserveReaderTaskAsync(NavigateReaderAiSourceAsync(source));
    }

    private async Task NavigateReaderAiSourceAsync(ReaderAiSourceViewModel source)
    {
        if (source.Page is { } page)
        {
            await NavigatePdfPageAsync(page.PageNumber, ReaderToken);
            return;
        }
        if (source.Chunk is not { } chunk || _readerDocument is null) return;
        var path = Path.GetFullPath(Path.Combine(
            _readerDocument.RootPath,
            chunk.ChapterPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathInside(_readerDocument.RootPath, path)) return;
        _readerPendingChunkOffset = Math.Max(0, chunk.StartOffset);
        _readerPendingSearchQuery = null;
        _readerPendingSearchContext = null;
        await NavigateToReaderItemAsync(
            new EpubReaderNavigationItem(chunk.ChapterTitle, new Uri(path).AbsoluteUri, chunk.ChapterIndex),
            ReaderToken,
            ReaderNavigationIntent.AiSource);
    }

    private static string LimitReaderContext(string value)
    {
        var normalized = value.Trim();
        return normalized.Length <= 2400 ? normalized : normalized[..2400] + "…";
    }

    private sealed record ReaderAiContext(string Text, IReadOnlyList<ReaderAiSourceViewModel> Sources);
}

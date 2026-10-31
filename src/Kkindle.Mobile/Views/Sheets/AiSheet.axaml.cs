using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile.Views.Sheets;

public sealed class AiMessage(bool isQuestion, string text) : ObservableObject
{
    private string _text = text;

    public bool IsQuestion { get; } = isQuestion;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }
}

/// <summary>
/// Questions about the page being read. Context is the visible text only: the
/// desktop's retrieval uses local embeddings, which the phone app does not ship.
/// </summary>
public partial class AiSheet : UserControl, IFullHeightSheet
{
    private const int MaxContextCharacters = 4000;

    private readonly ApplicationModules? _modules;
    private readonly MobileShell? _shell;
    private readonly Func<string>? _pageText;
    private readonly string? _selection;
    private readonly string _bookTitle = string.Empty;
    private readonly ObservableCollection<AiMessage> _messages = [];
    private readonly CancellationTokenSource _lifetime = new();
    private AiConnectionSettings? _settings;
    private bool _busy;

    /// <summary>Design-time only.</summary>
    public AiSheet() => InitializeComponent();

    public AiSheet(ApplicationModules modules, MobileShell shell, Book book, Func<string> pageText, string? selection)
    {
        _modules = modules;
        _shell = shell;
        _pageText = pageText;
        _selection = selection;
        _bookTitle = book.Title;
        InitializeComponent();
        Messages.ItemsSource = _messages;
        ExplainButton.IsVisible = !string.IsNullOrWhiteSpace(selection);
        Loaded += OnLoaded;
        DetachedFromVisualTree += (_, _) => _lifetime.Cancel();
    }

    public bool FillsHeight => true;

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _settings = await _modules!.Ai.Settings.LoadAsync(_lifetime.Token);
        var ready = _settings.IsConfigured;
        SetupPanel.IsVisible = !ready;
        IntroText.IsVisible = ready;
        QuickRow.IsVisible = ready;
        QuestionBox.IsEnabled = ready;
        SendButton.IsEnabled = ready;
        if (ready && _selection is not null) await AskAsync($"解释这段话：「{_selection}」");
    }

    private void OnSetupClick(object? sender, RoutedEventArgs e)
    {
        _shell?.HideSheet();
        _shell?.OpenAiSettings();
    }

    private async void OnSummarizeClick(object? sender, RoutedEventArgs e) =>
        await AskAsync("用几句话概括这一页的要点。");

    private async void OnExplainClick(object? sender, RoutedEventArgs e) =>
        await AskAsync($"解释这段话：「{_selection}」");

    private async void OnSendClick(object? sender, RoutedEventArgs e) => await SendTypedAsync();

    private async void OnQuestionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SendTypedAsync();
    }

    private async Task SendTypedAsync()
    {
        var question = QuestionBox.Text?.Trim();
        if (string.IsNullOrEmpty(question)) return;
        QuestionBox.Text = string.Empty;
        await AskAsync(question);
    }

    private async Task AskAsync(string question)
    {
        if (_busy || _settings is not { IsConfigured: true } settings) return;
        _busy = true;
        SendButton.IsEnabled = false;
        IntroText.IsVisible = false;
        var history = _messages
            .Select(message => new AiConversationTurn(message.IsQuestion ? "user" : "assistant", message.Text))
            .ToList();
        _messages.Add(new AiMessage(true, question));
        var answer = new AiMessage(false, "…");
        _messages.Add(answer);
        ScrollToEnd();

        var context = _pageText!();
        if (context.Length > MaxContextCharacters) context = context[..MaxContextCharacters];
        var instructions =
            $"你是 Kkindle 的阅读助手。读者正在读《{_bookTitle}》。请结合下面这段正文，用简洁的中文回答；"
            + "正文没有涉及时可以用常识补充，但要说明。\n\n正文：\n" + context;
        try
        {
            var text = new System.Text.StringBuilder();
            await foreach (var chunk in _modules!.Ai.ChatClient.StreamAsync(
                               settings, instructions, question, history, cancellationToken: _lifetime.Token))
            {
                text.Append(chunk.Text);
                answer.Text = text.ToString();
                ScrollToEnd();
            }
            if (text.Length == 0) answer.Text = "AI 没有返回内容。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            answer.Text = "请求失败：" + UiText.Localize(exception.Message);
        }
        finally
        {
            _busy = false;
            SendButton.IsEnabled = true;
        }
    }

    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
}

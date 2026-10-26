using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using CSharpMath.Avalonia;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class ReaderAiRenderingTests(SettingsUiSession session)
{
    [Fact]
    public Task MouseSelectionCopiesFormattedAnswerAndQuotesItWithoutLosingDraft() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ShowReaderAiTab");
        scope.Get<Control>("ReaderAiEmptyState").IsVisible = false;
        using var answer = new ReaderAiMessageViewModel("assistant", "名词加 -s 表示**复数**。\n\n动词加 -s 表示**单数**，两者含义不同。");
        scope.Window.ReaderAiMessages.Add(answer);
        await ReaderTests.Render();
        var text = scope.Get<Control>("ReaderAssistantPanel").GetVisualDescendants()
            .OfType<KreaderMarkdownTextBlock>().Single(block => block.Markdown == answer.Content);
        var selected = $"名词加 -s 表示复数。{Environment.NewLine}{Environment.NewLine}动词加 -s 表示单数";
        DragSelection(scope.Window, text, 0, selected.Length);
        await ReaderTests.Render();
        Assert.Equal(selected, text.SelectedText);
        var output = Environment.GetEnvironmentVariable("KKINDLE_READER_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            using var frame = scope.Window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(output, "ai-selection.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        var copy = scope.Window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "复制") && button.Classes.Contains("readerAiSelectionAction"));
        var copyPoint = copy.TranslatePoint(new Point(copy.Bounds.Width / 2, copy.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(copyPoint, MouseButton.Left);
        scope.Window.MouseUp(copyPoint, MouseButton.Left);
        await ReaderTests.Render();
        Assert.Equal(selected, await scope.Window.Clipboard!.TryGetTextAsync());

        var draft = scope.Get<TextBox>("ReaderAiQuestionBox");
        draft.Text = "为什么会这样？";
        scope.Set("_readerPendingSelection", "书籍原有选区");
        scope.Set("_readerAiBusy", true);
        DragSelection(scope.Window, text, 0, selected.Length);
        await ReaderTests.Render();
        var followUp = scope.Window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "追问"));
        var followUpPoint = followUp.TranslatePoint(new Point(followUp.Bounds.Width / 2, followUp.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(followUpPoint, MouseButton.Left);
        scope.Window.MouseUp(followUpPoint, MouseButton.Left);
        await ReaderTests.Render();
        Assert.Equal("针对这段对话：\n> 名词加 -s 表示复数。\n> \n> 动词加 -s 表示单数\n\n为什么会这样？", draft.Text);
        Assert.Same(draft, scope.Window.FocusManager!.GetFocusedElement());
        Assert.Equal(draft.Text.Length, draft.CaretIndex);
        Assert.Equal("书籍原有选区", scope.Field<string>("_readerPendingSelection"));
        Assert.Single(scope.Window.ReaderAiMessages);
        scope.Set("_readerAiBusy", false);
    });

    [Fact]
    public Task UserReasoningQuotesTablesAndCodeSupportSelectionContextActions() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ShowReaderAiTab");
        scope.Get<Control>("ReaderAiEmptyState").IsVisible = false;
        using var user = new ReaderAiMessageViewModel("user", "我的原始问题");
        using var answer = new ReaderAiMessageViewModel("assistant");
        answer.Update("> 引用中的**重点**\n\n| 参数 | 值 |\n| --- | --- |\n| 名称 | 内容 |\n\n```text\nkeeps\n```", "思考中的选文", false);
        answer.ToggleReasoning();
        scope.Window.ReaderAiMessages.Add(user);
        scope.Window.ReaderAiMessages.Add(answer);
        await ReaderTests.Render();
        var panel = scope.Get<Control>("ReaderAssistantPanel");
        foreach (var expected in new[] { "我的原始问题", "思考中的选文", "引用中的重点", "名称", "keeps" })
        {
            var text = panel.GetVisualDescendants().OfType<KreaderSelectableTextBlock>()
                .SingleOrDefault(block => block.IsEffectivelyVisible && (block.Inlines?.Text ?? block.Text)?.Trim() == expected);
            Assert.True(text is not null, $"Missing {expected}: {string.Join(" / ", panel.GetVisualDescendants().OfType<KreaderSelectableTextBlock>().Select(block => block.Inlines?.Text ?? block.Text))}");
            Assert.True(text.IsFollowUpEnabled, expected);
            text.Focus();
            text.SelectAll();
            text.ContextMenu!.Open(text);
            await ReaderTests.Render();
            var items = text.ContextMenu.Items.OfType<MenuItem>().ToArray();
            Assert.True(items.Single(item => Equals(item.Header, "复制")).IsEnabled, $"Menu lost selection for {expected}: {text.SelectedText}");
            items.Single(item => Equals(item.Header, "复制")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await ReaderTests.Render();
            Assert.Equal(text.SelectedText, await scope.Window.Clipboard!.TryGetTextAsync());
            items.Single(item => Equals(item.Header, "追问")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Contains("> " + expected, scope.Get<TextBox>("ReaderAiQuestionBox").Text);
            text.ContextMenu.Close();
            scope.Get<TextBox>("ReaderAiQuestionBox").Text = string.Empty;
        }
    });

    [Fact]
    public Task KeyboardCopyIncludesReadableCitationsFormulasAndEmbeddedBlocks() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ShowReaderAiTab");
        scope.Get<Control>("ReaderAiEmptyState").IsVisible = false;
        using var answer = new ReaderAiMessageViewModel("assistant", """
            **开头** [S1] [文档](https://example.com) $x^2$

            > 引用中的**重点**

            | 参数 | 值 |
            | --- | --- |
            | 名称 | 内容 |

            ```text
            keeps
            ```

            末尾
            """);
        scope.Window.ReaderAiMessages.Add(answer);
        await ReaderTests.Render();
        var text = scope.Get<Control>("ReaderAssistantPanel").GetVisualDescendants()
            .OfType<KreaderMarkdownTextBlock>().Single(block => block.Markdown == answer.Content);
        text.Focus();
        text.SelectAll();
        scope.Window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        await ReaderTests.Render();
        var copied = await scope.Window.Clipboard!.TryGetTextAsync();
        Assert.StartsWith("开头 [S1] 文档 $x^2$", copied);
        Assert.Contains("引用中的重点", copied);
        Assert.Contains("名称\t内容", copied);
        Assert.Contains("keeps", copied);
        Assert.EndsWith("末尾", copied);
        Assert.DoesNotContain("\uFFFC", copied);
        text.ContextMenu!.Open(text);
        text.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "追问"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Contains("> 开头 [S1] 文档 $x^2$", scope.Get<TextBox>("ReaderAiQuestionBox").Text);
    });

    [Fact]
    public Task UpdatingAnswerDismissesOldSelectionActions() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ShowReaderAiTab");
        scope.Get<Control>("ReaderAiEmptyState").IsVisible = false;
        using var answer = new ReaderAiMessageViewModel("assistant", "可以选择的旧回答");
        scope.Window.ReaderAiMessages.Add(answer);
        await ReaderTests.Render();
        var text = scope.Get<Control>("ReaderAssistantPanel").GetVisualDescendants()
            .OfType<KreaderMarkdownTextBlock>().Single(block => block.Markdown == answer.Content);
        DragSelection(scope.Window, text, 0, 4);
        await ReaderTests.Render();
        Assert.Contains(scope.Window.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "追问"));
        answer.Update("更新后的回答", string.Empty, false);
        await ReaderTests.Render();
        Assert.DoesNotContain(scope.Window.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "追问"));
        Assert.True(string.IsNullOrEmpty(text.SelectedText));
    });

    private static void DragSelection(Window window, SelectableTextBlock text, int start, int end)
    {
        Point Position(int index)
        {
            var rect = text.TextLayout.HitTestTextPosition(index);
            return text.TranslatePoint(new Point(rect.X + 0.1, rect.Y + rect.Height / 2), window)!.Value;
        }
        var first = Position(start);
        var last = Position(end);
        Assert.Same(text, window.InputHitTest(first));
        window.MouseMove(first);
        window.MouseDown(first, MouseButton.Left);
        window.MouseMove(last, RawInputModifiers.LeftMouseButton);
        Assert.True(!string.IsNullOrEmpty(text.SelectedText), $"No selection before release: {text.SelectionStart}/{text.SelectionEnd}; text={text.Inlines?.Text ?? text.Text}, first={first}, last={last}, focused={window.FocusManager!.GetFocusedElement()}");
        window.MouseUp(last, MouseButton.Left);
    }

    [Fact]
    public Task OverviewRetrievalStatusDoesNotTurnAReadyModelYellow() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Call("ApplyReaderEmbeddingModelStatus", true, true);
        scope.Call("SetReaderAiRetrievalStatus", "本次请求未使用向量模型。");
        var dot = scope.Get<Avalonia.Controls.Shapes.Ellipse>("ReaderAiVectorStatusDot");
        Assert.Equal(Color.Parse("#2E8B57"), Assert.IsAssignableFrom<ISolidColorBrush>(dot.Fill).Color);
        var tooltip = Assert.IsType<string>(ToolTip.GetTip(scope.Get<Button>("ReaderAiVectorStatusButton")));
        Assert.Contains("本地语义模型已就绪", tooltip);
        Assert.Contains("本次请求未使用向量模型", tooltip);
        scope.Call("ApplyReaderEmbeddingModelStatus", false, false);
        scope.Call("SetReaderAiRetrievalStatus", "尚未进行向量检索。");
        Assert.Equal(Color.Parse("#D6A100"), Assert.IsAssignableFrom<ISolidColorBrush>(dot.Fill).Color);
    });

    [Fact]
    public Task VectorStatusClickOpensSettingsAtTheEmbeddingModel() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ShowReaderAiTab");
        await ReaderTests.Render();
        var button = scope.Get<Button>("ReaderAiVectorStatusButton");
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), scope.Window)!.Value;
        var hit = scope.Window.InputHitTest(point) as Visual;
        Assert.True(ReferenceEquals(hit, button) || hit?.GetVisualAncestors().Contains(button) == true,
            $"Status button hit at {point}: {hit}; bounds {button.Bounds}, visible {button.IsEffectivelyVisible}");
        scope.Window.MouseMove(point);
        scope.Window.MouseDown(point, MouseButton.Left);
        scope.Window.MouseUp(point, MouseButton.Left);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (scope.Window.FocusManager!.GetFocusedElement() == scope.Get<ComboBox>("MainReaderEmbeddingModelSelectorBox")) break;
            await Task.Delay(20);
        }
        await ReaderTests.Render();
        Assert.True(scope.Get<Control>("SettingsPage").IsEffectivelyVisible);
        Assert.True(scope.Get<Expander>("SettingsAiExpander").IsExpanded);
        var selector = scope.Get<ComboBox>("MainReaderEmbeddingModelSelectorBox");
        Assert.Same(selector, scope.Window.FocusManager!.GetFocusedElement());
        var scroll = selector.GetVisualAncestors().OfType<ScrollViewer>().First();
        var position = selector.TranslatePoint(default, scroll)!.Value;
        Assert.InRange(position.Y, 0, scroll.Bounds.Height - selector.Bounds.Height);
    });

    [Fact]
    public Task AssistantCanBeDraggedPast640AndKeepsReadingSpaceWhenLayoutShrinks() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Window.Width = 1600;
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        var root = scope.Get<Grid>("ReaderRoot");
        root.IsVisible = true;
        scope.Set("_readerTocExpanded", false);
        scope.Set("_readerTocMinimal", false);
        scope.Call("ApplyReaderPanelLayout");
        scope.Call("ShowReaderAiTab");
        await ReaderTests.Render();
        var handle = scope.Get<Control>("ReaderAssistantResizeHandle");
        var first = handle.TranslatePoint(new Point(4, handle.Bounds.Height / 2), scope.Window)!.Value;
        var last = first - new Point(950, 0);
        scope.Window.MouseMove(first);
        scope.Window.MouseDown(first, MouseButton.Left);
        scope.Window.MouseMove(last);
        scope.Window.MouseUp(last, MouseButton.Left);
        await ReaderTests.Render();
        Assert.True(root.ColumnDefinitions[2].ActualWidth > 1000);
        Assert.True(root.ColumnDefinitions[1].ActualWidth >= 359);
        scope.Set("_readerTocExpanded", true);
        scope.Call("ApplyReaderPanelLayout");
        await ReaderTests.Render();
        Assert.True(root.ColumnDefinitions[1].ActualWidth >= 359);
        scope.Window.Width = 1024;
        await ReaderTests.Render();
        Assert.InRange(root.ColumnDefinitions[2].ActualWidth, 280, 400);
        Assert.True(root.ColumnDefinitions[1].ActualWidth >= 359);
    });

    [Fact]
    public Task MenuModelAndReasoningChoicesSurviveWindowRestart() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var connection = new AiConnectionSettings
        {
            Provider = "custom", BaseUrl = "http://127.0.0.1:11434/v1", Model = "first-model", ApiKey = "offline-test"
        };
        var aiStore = scope.Field<AiSettingsStore>("_aiSettingsStore");
        await aiStore.SaveAsync(connection);
        scope.Set("_readerAiSettings", connection);
        scope.Set("_readerAiAvailableModels", new[] { "second-model", "last-model" });
        scope.Call("ApplyReaderAiSettingsToControls");
        foreach (var model in new[] { "second-model", "last-model" })
            scope.Get<MenuItem>("ReaderAiModelMenuItem").Items.OfType<MenuItem>().Single(item => Equals(item.Tag, model))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        foreach (var depth in new[] { "low", "medium" })
            scope.Get<MenuItem>("ReaderAiReasoningMenuItem").Items.OfType<MenuItem>().Single(item => Equals(item.Tag, depth))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var appStore = new AppSettingsStore(scope.Paths);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((await aiStore.LoadAsync()).Model == "last-model"
                && (await appStore.LoadAsync()).ReaderAiReasoningDepth == "medium") break;
            await Task.Delay(20);
        }
        Assert.Equal("last-model", (await aiStore.LoadAsync()).Model);
        var saved = await appStore.LoadAsync();
        Assert.Equal("medium", saved.ReaderAiReasoningDepth);
        scope.Set("_allowWindowCloseForS3Sync", true);
        scope.Window.Close();
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.InitializeAsync();
        var restarted = new MainWindow(scope.Paths, library, startupSettings: saved);
        var reopened = new ReaderTestWindow(restarted, scope.Paths, scope.Paths.Root);
        try
        {
            restarted.Show();
            await reopened.Call<Task>("InitializeReaderAiAsync", CancellationToken.None);
            await ReaderTests.Render();
            Assert.Equal("last-model", reopened.Field<AiConnectionSettings>("_readerAiSettings").Model);
            Assert.Equal("offline-test", reopened.Field<AiConnectionSettings>("_readerAiSettings").ApiKey);
            Assert.Equal("medium", reopened.Field<string>("_readerAiReasoningDepth"));
            Assert.True(reopened.Get<MenuItem>("ReaderAiModelMenuItem").Items.OfType<MenuItem>()
                .Single(item => Equals(item.Tag, "last-model")).IsChecked);
            Assert.True(reopened.Get<MenuItem>("ReaderAiReasoningMenuItem").Items.OfType<MenuItem>()
                .Single(item => Equals(item.Tag, "medium")).IsChecked);
        }
        finally
        {
            reopened.Set("_allowWindowCloseForS3Sync", true);
            restarted.Close();
        }
    });

    [Fact]
    public Task CitationHoverShowsScrollableEvidenceAndClosesWhenAnswerChanges() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ShowReaderAiTab");
        scope.Get<Control>("ReaderAiEmptyState").IsVisible = false;
        ReaderAiSourceViewModel? navigated = null;
        using var answer = new ReaderAiMessageViewModel("assistant", "参考路径说明。[S1] [S99]",
            citationAction: source => navigated = source);
        var evidence = string.Join('\n', Enumerable.Repeat("OSCin → Pre-R → MULT → Post-R → PFD。原文全文应可滚动阅读。", 35));
        answer.SetSources([new ReaderAiSourceViewModel(new PdfPageText(19, evidence))]);
        scope.Window.ReaderAiMessages.Add(answer);
        await ReaderTests.Render();
        var marker = scope.Get<Control>("ReaderAssistantPanel").GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "[S1]"));
        var point = marker.TranslatePoint(new Point(marker.Bounds.Width / 2, marker.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseMove(point);
        await Task.Delay(400);
        await ReaderTests.Render();
        var original = scope.Window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(text => text.Text == evidence);
        Assert.Contains(scope.Window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "[S1] 第 19 页");
        var scroll = original.GetVisualAncestors().OfType<ScrollViewer>().First();
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        var cardPoint = original.TranslatePoint(new Point(10, 10), scope.Window)!.Value;
        scope.Window.MouseMove(cardPoint);
        await Task.Delay(350);
        await ReaderTests.Render();
        Assert.True(original.IsAttachedToVisualTree());
        scroll.Offset = new Vector(0, 100);
        await ReaderTests.Render();
        Assert.True(scroll.Offset.Y > 0);
        Assert.Null(navigated);
        marker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(19, navigated!.Page!.PageNumber);
        Assert.False(original.IsAttachedToVisualTree());
        scope.Window.MouseMove(new Point(20, 20));
        scope.Window.MouseMove(point);
        await Task.Delay(400);
        await ReaderTests.Render();
        answer.Update("更新后的回答", string.Empty, false);
        await ReaderTests.Render();
        Assert.DoesNotContain(scope.Window.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.Text == evidence);
    });

    [Fact]
    public Task ExpandedAssistantKeepsConstantSideMargins() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Window.Width = 1600;
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ShowReaderAiTab");
        scope.Get<Control>("ReaderAiEmptyState").IsVisible = false;
        using var answer = new ReaderAiMessageViewModel("assistant", new string('文', 200));
        scope.Window.ReaderAiMessages.Add(answer);
        foreach (var width in new[] { 280d, 380d, 640d, 900d })
        {
            scope.Call("SetReaderAiPanelWidth", width);
            await ReaderTests.Render();
            var panel = scope.Get<Control>("ReaderAssistantPanel");
            var block = panel.GetVisualDescendants().OfType<KreaderMarkdownTextBlock>().Single(text => text.Markdown == answer.Content);
            Assert.InRange(block.TranslatePoint(default, panel)!.Value.X, 29, 31);
            Assert.InRange(block.Bounds.Width, width - 75, width - 59);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SelectionExplainOpensCollapsedAssistantAndLeavesNotes(bool zen) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ShowReaderNotesTab");
        scope.Get<Control>("ReaderAssistantPanel").IsVisible = false;
        scope.Get<Grid>("ReaderRoot").ColumnDefinitions[2].Width = new GridLength(0);
        if (zen) scope.Call("ToggleReaderZenMode");
        scope.Set("_appSettings", scope.Field<AppSettings>("_appSettings") with { AiEnabled = true, NetworkEnabled = true });
        scope.Set("_readerAiSettings", new AiConnectionSettings { ApiKey = "offline-test" });
        // An answer is already running: this exercises the actual popup click
        // without initiating another request or requiring network credentials.
        scope.Set("_readerAiBusy", true);
        scope.Set("_readerPendingSelection", "Reference path frequency");
        scope.Call("ShowReaderSelectionPopup", new Point(30, 120), 140d);
        await ReaderTests.Render();
        var popup = scope.Get<Border>("ReaderSelectionPopupBar");
        var explain = popup.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "AI 解释"));
        explain.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await ReaderTests.Render();
        Assert.True(scope.Get<Control>("ReaderAssistantPanel").IsVisible);
        Assert.True(scope.Get<Control>("ReaderAiView").IsVisible);
        Assert.True(scope.Get<Control>("ReaderAiComposer").IsVisible);
        Assert.False(scope.Get<Control>("ReaderNotesView").IsVisible);
        Assert.False(scope.Field<bool>("_readerZenMode"));
        Assert.InRange(scope.Get<Grid>("ReaderRoot").ColumnDefinitions[2].ActualWidth, 280, 640);
        Assert.False(scope.Get<Avalonia.Controls.Primitives.Popup>("ReaderSelectionHostPopup").IsOpen);
        scope.Set("_readerAiBusy", false);
    });

    [Fact]
    public Task RichAnswerExpandsLinesAndScrollsWideBlocksAtMinimumWidth() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ShowReaderAiTab");
        scope.Call("SetReaderAiPanelWidth", 280d);
        scope.Get<Control>("ReaderAiEmptyState").IsVisible = false;
        using var answer = new ReaderAiMessageViewModel("assistant");
        answer.Update("""
            ## **注意：**中文强调

            \[f_{PD}=\frac{f_{OSC}\times OSC\_2X\times MULT}{PLL\_R\_PRE\times PLL\_R}\]

            | 参数 | 含义 | 示例 |
            | --- | --- | --- |
            | OSC_2X | 输入倍频 | $2$ |
            | PLL_R_PRE | 预分频器 | $1$ |
            | PLL_R | 后分频器 | $4$ |

            ```csharp
            var frequency = oscillator * doubler * multiplier / (preDivider * postDivider);
            ```

            末尾应显示在表格和代码下方。
            """, string.Empty, false);
        scope.Window.ReaderAiMessages.Add(answer);
        await ReaderTests.Render();
        var block = scope.Get<Control>("ReaderAssistantPanel").GetVisualDescendants()
            .OfType<KreaderMarkdownTextBlock>().Single(text => text.Markdown == answer.Content);
        Assert.True(double.IsNaN(block.LineHeight), "Fixed line height clips tall formulas and overlaps block elements.");
        Assert.Contains(block.TextLayout.TextLines, line => line.Height > 100);
        var formula = block.GetVisualDescendants().OfType<MathView>().First();
        Assert.True(formula.Bounds.Height > 20);
        var formulaBox = Assert.IsType<Viewbox>(formula.Parent);
        Assert.InRange(formulaBox.Bounds.Width, 1, block.Bounds.Width);
        var scrolls = block.GetVisualDescendants().OfType<ScrollViewer>().ToArray();
        Assert.Equal(2, scrolls.Length);
        Assert.All(scrolls, scroll => Assert.True(scroll.Extent.Width > scroll.Viewport.Width));
        foreach (var scroll in scrolls)
        {
            scroll.Offset = new Vector(100, 0);
            await ReaderTests.Render();
            Assert.True(scroll.Offset.X > 0);
        }
        // Brushes must update existing math and code when the theme changes.
        block.Foreground = Brushes.White;
        await ReaderTests.Render();
        Assert.Equal(Colors.White, formula.TextColor);
        Assert.All(block.GetVisualDescendants().OfType<SelectableTextBlock>(),
            code => Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(code.Foreground).Color));
        Assert.Contains(block.Inlines!.OfType<Span>().First().Inlines.OfType<Span>(),
            span => span.FontWeight == FontWeight.Bold);
    });

    private Task Run(Func<Task> action) => session.Session.Dispatch(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);
}

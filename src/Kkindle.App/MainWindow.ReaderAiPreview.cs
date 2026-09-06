#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    // Explicitly opt-in, offline visual regression fixture. Uses the actual
    // templates and layout, without sending requests or changing saved settings.
    private async Task RunReaderAiPreviewAsync()
    {
        var output = Environment.GetEnvironmentVariable("KKINDLE_AI_PREVIEW_OUTPUT")
            ?? Path.Combine(_paths.Logs, "ai-preview");
        Directory.CreateDirectory(output);
        var exitCode = 0;
        try
        {
            Width = 1200;
            Height = 850;
            LibraryRoot.IsVisible = false;
            ReaderRoot.IsVisible = true;
            ReaderAssistantPanel.IsVisible = true;
            ShowReaderAiTab();
            ApplyReaderAiModelSelectors("deepseek", "deepseek-chat");
            UpdateReaderAiReasoningDepthSelector();
            ClearReaderAiConversation();
            ReaderAiStatusText.Text = "Ctrl+Enter 发送 · Enter 换行";
            await CaptureAsync("empty", 380);

            ReaderAiEmptyState.IsVisible = false;
            ReaderAiMessages.Add(new ReaderAiMessageViewModel("user", "这一章的核心观点是什么？"));
            var answer = new ReaderAiMessageViewModel("assistant", citationAction: HandleReaderAiCitation);
            answer.SetSources([new ReaderAiSourceViewModel(new PdfPageText(12, "习惯的积累改变了长期结果。"), "S1")]);
            answer.Update("## 让行动更容易发生\n\n这一章强调：**微小而持续的行动，比短期的决心更重要。** [S1]\n\n- 降低开始行动的难度。\n- 用稳定的环境提示建立习惯。\n- 关注每天的重复，而不是一次做到完美。\n\n> 可以先问自己：今天最容易完成的一小步是什么？", "已结合本章原文整理。", false);
            answer.CanRetry = true;
            ReaderAiMessages.Add(answer);
            foreach (var width in new[] { 280, 380, 640 })
                await CaptureAsync("answer", width);
            // The input must survive failed preflight, including keyboard send.
            var settings = _appSettings;
            _appSettings = _appSettings with { AiEnabled = false };
            ReaderAiQuestionBox.Text = "未配置时必须保留的问题";
            await SendReaderAiQuestionAsync(ReaderAiQuestionBox.Text, clearDraft: true);
            if (ReaderAiQuestionBox.Text != "未配置时必须保留的问题")
                throw new InvalidOperationException("Draft lost during preflight.");
            _appSettings = settings;
            ReaderAiQuestionBox.Text = string.Empty;
            ReaderAiStatusText.Text = "Ctrl+Enter 发送 · Enter 换行";
            await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), "PASS: layout 280/380/640; preflight retains draft.");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), exception.ToString());
        }
        finally { Environment.Exit(exitCode); }

        async Task CaptureAsync(string name, int width)
        {
            SetReaderAiPanelWidth(width);
            await Task.Delay(250);
            if (ReaderAiQuestionBox.Bounds.Width <= 0 || ReaderAiSendButton.Bounds.Width < 30)
                throw new InvalidOperationException("Composer did not lay out.");
            if (name == "answer" && !ReaderAssistantPanel.GetVisualDescendants()
                .OfType<KreaderMarkdownTextBlock>().Any(block => block.Bounds.Height > 100 && block.Inlines?.Count > 0))
                throw new InvalidOperationException("Markdown answer was not rendered.");
            using var bitmap = new RenderTargetBitmap(new PixelSize(
                (int)Math.Ceiling(ReaderAssistantPanel.Bounds.Width),
                (int)Math.Ceiling(ReaderAssistantPanel.Bounds.Height)));
            bitmap.Render(ReaderAssistantPanel);
            bitmap.Save(Path.Combine(output, $"{name}-{width}.png"), PngBitmapEncoderOptions.Default);
        }
    }
}
#endif

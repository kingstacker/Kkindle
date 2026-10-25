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
            Width = 1400;
            Height = 850;
            LibraryRoot.IsVisible = false;
            ReaderRoot.IsVisible = true;
            ReaderAssistantPanel.IsVisible = true;
            ReaderAssistantPanel.Background = (Avalonia.Media.IBrush)Resources["ReaderPageBrush"]!;
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

            answer.Update("公式正在生成：\\(\\frac{1}{", string.Empty, true);
            await CaptureAsync("streaming", 280);
            answer.Update("""
                ## 参考路径与鉴相频率

                从 OSCin 输入得到鉴相频率 \(f_{PD}\)。**公式中的下标应完整显示**。[S1]

                \[
                f_{PD}=\frac{f_{OSC}\times OSC\_2X\times MULT}{PLL\_R\_PRE\times PLL\_R}
                \]

                ### 简单例子

                假设 \(f_{OSC}=10\text{ MHz}\)，倍频为 $2$，预分频为 $1$，后分频为 $4$：

                $$f_{PD}=\frac{10\text{ MHz}\times2\times1}{1\times4}=5\text{ MHz}$$

                > **注意：**行内公式 \(x^2+y^2=z^2\) 和引用内格式也应正确。[S1]
                """, string.Empty, false);
            foreach (var width in new[] { 280, 380, 640 })
                await CaptureAsync("math", width);

            answer.Update("""
                ## **格式**与列表

                3. **有序列表**保留起始序号。
                   - 子项目使用 *斜体*，再嵌套 **粗体与 *斜体***。
                4. 下一项含 ~~删除线~~ 和 `inline_code`。

                - [ ] 待办事项
                - [x] 已完成事项

                > 引用中的 **强调**、[链接](https://example.com) 和 \(f_{OSC}\) 应保留。[S1]

                | 参数 | 含义 | 示例 |
                | :--- | :---: | ---: |
                | **OSC_2X** | 输入倍频 | $2$ |
                | PLL_R_PRE | 预分频器 | $1$ |
                | PLL_R | 后分频器 | $4$ |

                ```csharp
                // 代码保留原样：$literal$、[S1] 和 \(x\)
                var frequency = oscillator * doubler * multiplier / (preDivider * postDivider);
                ```

                普通文本：价格 $5 和 $10，不应变成公式。&lt;tag&gt; &amp; **末尾**。
                """, "思考过程也支持 **强调** 与 \\(f_{PD}\\)。", false);
            foreach (var width in new[] { 280, 380, 640 })
            {
                await CaptureAsync("format", width);
                ReaderAiScrollViewer.ScrollToEnd();
                await CaptureAsync("format-bottom", width, resetScroll: false);
            }
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
            answer.SetSources([
                new ReaderAiSourceViewModel(new PdfPageText(19, string.Join("\n\n", Enumerable.Repeat(
                    "7.3.2 Reference Path\nThe reference path consists of an OSCin doubler (OSC_2X), Pre-R divider, multiplier (MULT) and a Post-R divider.\nThe OSCin doubler can double up low OSCin frequencies. Pre-R and Post-R dividers both divide frequency down while the multiplier multiplies frequency up.", 5))), "S1"),
                new ReaderAiSourceViewModel(new PdfPageText(20, "Reference frequency settings: check the input clock and divider configuration."), "S2")]);
            answer.Update("""
                ## 参考路径与鉴相频率

                **含义：**参考路径由输入倍频器、预分频器、倍频器和后分频器组成。[S1]

                \(f_{PD}=\frac{f_{OSC}\times OSC\_2X\times MULT}{PLL\_R\_PRE\times PLL\_R}\) [S1]

                **上下文：**参考输入经过各级倍频和分频后送入鉴相器。[S2]

                鼠标停在参考编号上可查看页码与原文，长原文可在浮窗中滚动。
                """, string.Empty, false);
            await CaptureAsync("citations", 640);
            await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), "PASS: Markdown/math/streaming layout 280/380/640; preflight retains draft.");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), exception.ToString());
        }
        finally
        {
            if (exitCode != 0 || Environment.GetEnvironmentVariable("KKINDLE_AI_PREVIEW_KEEP_OPEN") != "1")
                Environment.Exit(exitCode);
        }

        async Task CaptureAsync(string name, int width, bool resetScroll = true)
        {
            SetReaderAiPanelWidth(width);
            if (resetScroll) ReaderAiScrollViewer.Offset = default;
            await Task.Delay(250);
            if (Math.Abs(ReaderAssistantPanel.Bounds.Width - width) > 1)
                throw new InvalidOperationException($"Expected panel width {width}, got {ReaderAssistantPanel.Bounds.Width}.");
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

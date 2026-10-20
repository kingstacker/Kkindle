using Avalonia.Controls;
using Avalonia.Media;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task LongReaderTitleTooltipShowsEveryLineWithoutEllipsis() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        var title = scope.Get<TextBlock>("ReaderBookInfoText");
        title.Text = "LMX2594 15GHz 宽带 PLLATINUM™ 射频合成器，支持相位同步和 JESD204B："
            + string.Concat(Enumerable.Repeat("完整书籍标题与技术说明 ", 12)) + "标题结束";
        await Render();
        Assert.Contains(title.TextLayout.TextLines, line => line.HasCollapsed);
        scope.Call("UpdateReaderBookTitleToolTip");
        ToolTip.SetIsOpen(title, true);
        await Render();
        try
        {
            var tip = Assert.IsType<TextBlock>(ToolTip.GetTip(title));
            Assert.Equal(title.Text, tip.Text);
            Assert.Equal(0, tip.MaxLines);
            Assert.Equal(TextTrimming.None, tip.TextTrimming);
            Assert.True(tip.TextLayout.TextLines.Count > 1);
            Assert.DoesNotContain(tip.TextLayout.TextLines, line => line.HasCollapsed);
            Assert.True(tip.TextLayout.TextLines.Sum(line => line.Length) >= title.Text.Length);
            var output = Environment.GetEnvironmentVariable("KKINDLE_SETTINGS_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    new Avalonia.PixelSize((int)Math.Ceiling(tip.Bounds.Width), (int)Math.Ceiling(tip.Bounds.Height)));
                bitmap.Render(tip);
                bitmap.Save(Path.Combine(output, "reader-full-title.png"));
            }
            Capture(scope.Window, "reader-long-title-tooltip");
        }
        finally
        {
            ToolTip.SetIsOpen(title, false);
        }
        title.Text = "短标题";
        await Render();
        Assert.Null(ToolTip.GetTip(title));
    });
}

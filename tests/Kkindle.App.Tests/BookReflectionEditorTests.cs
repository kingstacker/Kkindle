using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kkindle.Core;
using SkiaSharp;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task NativeReflectionEditorShowsFocusedTextAndUsesFixedToolbar() => Run(async () =>
    {
        var surface = new BookReflectionEditorSurface("这段文字应该显示出来")
        {
            Width = 620,
            Height = 120
        };
        var window = new Window
        {
            Width = 720,
            Height = 500,
            Content = new StackPanel { Children = { surface.FormattingToolbar, surface } }
        };
        window.Show();
        try
        {
            await Render();
            surface.FocusEditor();
            await Render();

            using var frame = new RenderTargetBitmap(new PixelSize(620, 120));
            frame.Render(surface);
            using var png = new MemoryStream();
            frame.Save(png, PngBitmapEncoderOptions.Default);
            png.Position = 0;
            using var pixels = SKBitmap.Decode(png);
            var darkPixels = 0;
            for (var y = 0; y < Math.Min(30, pixels.Height); y++)
            {
                for (var x = 0; x < Math.Min(240, pixels.Width); x++)
                {
                    var pixel = pixels.GetPixel(x, y);
                    if (pixel.Red < 160 && pixel.Green < 160 && pixel.Blue < 160)
                        darkPixels++;
                }
            }
            Assert.True(darkPixels > 10, "Focused editor text should remain visible.");

            var editor = Assert.Single(surface.GetVisualDescendants().OfType<TextBox>());
            editor.SelectionStart = 2;
            editor.SelectionEnd = 8;
            await Render();
            Assert.Empty(surface.GetVisualDescendants().OfType<Popup>());
            Assert.True(surface.FormattingToolbar.IsEffectivelyVisible);
            var toolbarButtons = surface.FormattingToolbar.GetVisualDescendants()
                .OfType<Button>()
                .ToArray();
            Assert.Equal(9, toolbarButtons.Length);
            Assert.All(toolbarButtons, button =>
                Assert.Equal(toolbarButtons[0].Bounds.Width, button.Bounds.Width));
            var bold = toolbarButtons.Single(button => button.Tag as string == "bold");
            var quote = toolbarButtons.Single(button => button.Tag as string == "quote");
            Assert.IsType<Viewbox>(bold.Content);
            Assert.IsType<Viewbox>(quote.Content);
            Assert.Single(bold.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
            Assert.Single(quote.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
            bold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Render();
            Assert.Equal(editor.SelectionStart, editor.SelectionEnd);
            Assert.Contains("**文字应该显示**", surface.Markdown);
        }
        finally
        {
            surface.Dispose();
            window.Close();
        }
    });

    [Fact]
    public Task NativeReflectionQuoteIsEditableAndKeepsCaretOffsets() => Run(async () =>
    {
        var surface = new BookReflectionEditorSurface("> 原来的引用")
        {
            Width = 620,
            Height = 220
        };
        var window = new Window
        {
            Width = 720,
            Height = 500,
            Content = surface
        };
        window.Show();
        try
        {
            await Render();
            var editor = Assert.Single(surface.GetVisualDescendants().OfType<TextBox>());
            Assert.Equal("原来的引用", editor.Text);
            editor.Focus();
            editor.CaretIndex = 3;
            editor.Text = "新的引用";
            await Render();
            Assert.Equal("新的引用", editor.Text);
            Assert.Equal(3, editor.CaretIndex);
            Assert.Equal("> 新的引用", surface.Markdown);

            editor.CaretIndex = 1;
            var quoteButton = surface.FormattingToolbar.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Tag as string == "quote");
            quoteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Render();
            Assert.Equal(1, editor.CaretIndex);
            Assert.Equal("> 新的引用", surface.Markdown);
        }
        finally
        {
            surface.Dispose();
            window.Close();
        }
    });

    [Fact]
    public Task NativeReflectionQuoteAdornmentAndWidthFollowText() => Run(async () =>
    {
        var surface = new BookReflectionEditorSurface("> 短引用")
        {
            Width = 620,
            Height = 220
        };
        var window = new Window
        {
            Width = 720,
            Height = 500,
            Content = surface
        };
        window.Show();
        try
        {
            await Render();

            var opening = Assert.Single(
                surface.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "“");
            var closing = Assert.Single(
                surface.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "”");
            var contentHost = Assert.IsType<Grid>(opening.Parent);
            var editor = Assert.Single(surface.GetVisualDescendants().OfType<TextBox>());
            var shortWidth = contentHost.Bounds.Width;

            Assert.True(opening.Bounds.Width >= 28, "The opening quote must have enough room to render fully.");
            Assert.True(closing.Bounds.Width >= 28, "The closing quote must remain visible.");
            Assert.True(shortWidth < surface.Bounds.Width - 40,
                "A short quote background should follow its text instead of filling the editor width.");
            Assert.InRange(shortWidth - closing.Bounds.Right, 2, 4);

            editor.Text = "这是一段明显更长的引用文字，用来确认背景宽度会跟随内容自动变化";
            await Render();
            Assert.True(contentHost.Bounds.Width > shortWidth + 100,
                "The quote background should grow when its text grows.");
        }
        finally
        {
            surface.Dispose();
            window.Close();
        }
    });

    [Fact]
    public Task NativeReflectionEditorWindowRendersAutoWidthQuote() => Run(async () =>
    {
        var window = new BookReflectionEditorWindow("测试书", "> 短引用")
        {
            Width = 720,
            Height = 520
        };
        window.Show();
        try
        {
            await Render();

            var opening = Assert.Single(
                window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "“");
            var closing = Assert.Single(
                window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "”");
            var contentHost = Assert.IsType<Grid>(opening.Parent);
            Assert.True(opening.Bounds.Width >= 28);
            Assert.True(closing.Bounds.Width >= 28);
            Assert.True(contentHost.Bounds.Width < window.Bounds.Width - 120,
                "The real editor window must keep a short quote background content-sized.");

            var captureDirectory = Environment.GetEnvironmentVariable("KKINDLE_REFLECTION_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                using var frame = new RenderTargetBitmap(
                    new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
                frame.Render(window);
                frame.Save(
                    Path.Combine(captureDirectory, "reflection-editor-quote-auto-width.png"),
                    PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ReflectionCitationSearchMatchesAllFields()
    {
        var first = new BookReflectionCitation(
            Guid.NewGuid(), "书一", "第一章", "被选中的句子", "我的批注", DateTimeOffset.UtcNow.AddMinutes(-1));
        var second = new BookReflectionCitation(
            Guid.NewGuid(), "书二", "第二章", "另一段", "重点结论", DateTimeOffset.UtcNow);

        Assert.Equal(second, BookReflectionEditorSurface.FilterCitations([first, second], "重点").Single());
        Assert.Equal(first, BookReflectionEditorSurface.FilterCitations([first, second], "第一章").Single());
        Assert.Equal(2, BookReflectionEditorSurface.FilterCitations([first, second], null).Count);
    }

    [Fact]
    public void ReadingMaterialReflectionUsesMarkdownPreviewContent()
    {
        var reflection = new ReaderBookReflection
        {
            BookId = Guid.NewGuid(),
            Content = "\"# 标题\\n\\n**重点**\""
        };
        var item = new Stage3ReadingMaterialViewModel(
            ReadingMaterialSource.Local,
            "测试书",
            "读后思考",
            "读后思考",
            "书籍级",
            string.Empty,
            reflection.Content,
            reflection.UpdatedAt,
            null,
            null,
            localReflection: reflection);
        try
        {
            Assert.Equal("# 标题\n\n**重点**", item.ReflectionMarkdown);
            Assert.False(item.IsNotBookReflection);
            Assert.Empty(item.SelectedContentLabel);
        }
        finally
        {
            item.Dispose();
        }
    }

    [Fact]
    public Task NativeReflectionImagesCanBeSizedAndDeleted() => Run(async () =>
    {
        const string pixel =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var surface = new BookReflectionEditorSurface(
            $"![original](data:image/png;base64,{pixel} =80x80)")
        {
            Width = 620,
            Height = 400
        };
        var window = new Window
        {
            Width = 720,
            Height = 500,
            Content = surface
        };
        window.Show();
        try
        {
            await Render();

            var image = Assert.Single(surface.GetVisualDescendants().OfType<Image>());
            Assert.True(image.IsVisible);
            Assert.Equal(80, image.Bounds.Width, 1);
            Assert.Equal(80, image.Bounds.Height, 1);

            var actions = surface.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => AutomationProperties.GetName(button) is not null)
                .ToDictionary(button => AutomationProperties.GetName(button)!);
            Assert.Contains("放大图片", actions.Keys);
            Assert.Contains("删除图片", actions.Keys);

            actions["放大图片"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Render();
            Assert.Matches(@" =\d+x\d+\)$", surface.Markdown);
            Assert.True(image.Bounds.Width > 80);

            actions["删除图片"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Render();
            Assert.DoesNotContain("![", surface.Markdown, StringComparison.Ordinal);
            Assert.NotEmpty(surface.GetVisualDescendants().OfType<TextBox>());
        }
        finally
        {
            surface.Dispose();
            window.Close();
        }
    });

    [Fact]
    public Task NativeReflectionEditorPastesClipboardImage() => Run(async () =>
    {
        const string pixel =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var surface = new BookReflectionEditorSurface("在这里粘贴")
        {
            Width = 620,
            Height = 400
        };
        var window = new Window
        {
            Width = 720,
            Height = 500,
            Content = surface
        };
        using var clipboardBitmap = new Bitmap(
            new MemoryStream(Convert.FromBase64String(pixel), writable: false));
        window.Show();
        try
        {
            await Render();
            var editor = Assert.Single(surface.GetVisualDescendants().OfType<TextBox>());
            editor.Focus();
            editor.CaretIndex = editor.Text?.Length ?? 0;
            await window.Clipboard!.SetBitmapAsync(clipboardBitmap);

            editor.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.V,
                KeyModifiers = KeyModifiers.Control
            });
            await Until(() => surface.Markdown.Contains("data:image/png;base64", StringComparison.Ordinal));
            // The alt text is localised, so compare against whichever language is
            // active rather than hardcoding the Chinese string: the suite also
            // runs on hosts whose system language is English.
            Assert.Contains(UiText.Get("粘贴的图片"), surface.Markdown, StringComparison.Ordinal);
            Assert.Single(surface.GetVisualDescendants().OfType<Image>());

            await window.Clipboard!.SetTextAsync("普通文字粘贴");
            var textEditor = surface.GetVisualDescendants().OfType<TextBox>().Last();
            textEditor.Focus();
            textEditor.CaretIndex = textEditor.Text?.Length ?? 0;
            textEditor.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.V,
                KeyModifiers = KeyModifiers.Control
            });
            await Until(() => surface.Markdown.Contains("普通文字粘贴", StringComparison.Ordinal));
        }
        finally
        {
            surface.Dispose();
            window.Close();
        }
    });

    [Fact]
    public Task ReflectionPreviewHonorsMaxLines() => Run(async () =>
    {
        var preview = new KreaderMarkdownTextBlock
        {
            Width = 300,
            MaxLines = 6,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            Markdown = string.Join("\n", Enumerable.Range(1, 20).Select(index => $"第 {index} 行"))
        };
        var window = new Window
        {
            Width = 400,
            Height = 500,
            Content = preview
        };
        window.Show();
        try
        {
            await Render();
            Assert.Equal(6, preview.MaxLines);
            Assert.NotNull(preview.TextLayout);
            Assert.InRange(preview.TextLayout!.Height, 0, preview.FontSize * 12);
            Assert.Equal(20, preview.Markdown!.Split('\n').Length);
        }
        finally
        {
            window.Close();
        }
    });
}

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

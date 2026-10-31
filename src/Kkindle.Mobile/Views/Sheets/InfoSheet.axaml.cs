using Avalonia.Controls;

namespace Kkindle.Mobile.Views.Sheets;

/// <summary>Read-only text such as a footnote or a translation, which may arrive later.</summary>
public partial class InfoSheet : UserControl
{
    /// <summary>Design-time only.</summary>
    public InfoSheet() => InitializeComponent();

    public InfoSheet(string body, string? source = null)
    {
        InitializeComponent();
        Present(body, source);
    }

    public void Present(string body, string? source = null)
    {
        BodyText.Text = body;
        SourceText.Text = source;
        SourceText.IsVisible = !string.IsNullOrWhiteSpace(source);
    }
}

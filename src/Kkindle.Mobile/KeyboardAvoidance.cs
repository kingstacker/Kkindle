using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Kkindle.Mobile;

/// <summary>
/// Keeps a form's focused field above the on-screen keyboard. The viewport
/// itself must end at the keyboard: with only extra content padding, a covered
/// field still counts as in view and nothing scrolls. A tap outside a field
/// drops focus, otherwise any tap would reopen the keyboard.
/// </summary>
internal static class KeyboardAvoidance
{
    public static void Attach(UserControl page, ScrollViewer form, Control actionBar)
    {
        IInputPane? pane = null;
        void OnChanged(object? sender, InputPaneStateEventArgs e)
        {
            var open = e.NewState == InputPaneState.Open;
            var covered = open ? Math.Max(0, e.EndRect.Height - actionBar.Bounds.Height) : 0;
            form.Margin = new Thickness(0, 0, 0, covered);
            if (open) Reveal(page);
        }

        page.Focusable = true;
        page.Loaded += (_, _) =>
        {
            pane = TopLevel.GetTopLevel(page)?.InputPane;
            if (pane is not null) pane.StateChanged += OnChanged;
        };
        page.DetachedFromVisualTree += (_, _) =>
        {
            if (pane is not null) pane.StateChanged -= OnChanged;
            pane = null;
        };
        page.AddHandler(InputElement.GotFocusEvent, (_, _) => Reveal(page));
        page.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is null)
                page.Focus();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void Reveal(Control page) =>
        // After the layout pass that applies the new viewport.
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(page)?.FocusManager?.GetFocusedElement() is TextBox { Parent: Control field })
                field.BringIntoView();
        }, DispatcherPriority.ContextIdle);
}

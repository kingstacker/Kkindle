using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Kkindle;

/// <summary>
/// Starts a resize gesture from one of the custom window-edge controls.
/// Windows and Linux hand the drag to the windowing system. Avalonia's macOS
/// backend returns from <see cref="Window.BeginResizeDrag"/> without resizing,
/// so the frame is updated from pointer deltas there instead.
/// </summary>
internal sealed class WindowEdgeResizer
{
    private readonly Window _window;
    private ResizeSession? _session;

    public WindowEdgeResizer(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
    }

    public bool TryBegin(Control source, string edge, PointerPressedEventArgs e)
    {
        if (!_window.CanResize
            || _window.WindowState != WindowState.Normal
            || !TryParseEdge(edge, out var windowEdge, out var horizontal, out var vertical))
            return false;

        if (!OperatingSystem.IsMacOS())
        {
            _window.BeginResizeDrag(windowEdge, e);
            return true;
        }

        _session?.Dispose();
        _session = new ResizeSession(_window, source, horizontal, vertical, e);
        return true;
    }

    private static bool TryParseEdge(
        string edge,
        out WindowEdge windowEdge,
        out int horizontal,
        out int vertical)
    {
        // horizontal: -1 moves the west edge, 1 the east edge, 0 unchanged.
        (windowEdge, horizontal, vertical) = edge switch
        {
            "Left" => (WindowEdge.West, -1, 0),
            "Right" => (WindowEdge.East, 1, 0),
            "Top" => (WindowEdge.North, 0, -1),
            "Bottom" => (WindowEdge.South, 0, 1),
            "TopLeft" => (WindowEdge.NorthWest, -1, -1),
            "TopRight" => (WindowEdge.NorthEast, 1, -1),
            "BottomLeft" => (WindowEdge.SouthWest, -1, 1),
            "BottomRight" => (WindowEdge.SouthEast, 1, 1),
            _ => (default, 0, 0)
        };
        return horizontal != 0 || vertical != 0;
    }

    private sealed class ResizeSession : IDisposable
    {
        private readonly Window _window;
        private readonly Control _source;
        private readonly int _horizontal;
        private readonly int _vertical;
        private readonly PixelPoint _startScreenPointer;
        private readonly PixelPoint _startPosition;
        private readonly Size _startSize;
        private bool _finished;

        public ResizeSession(
            Window window,
            Control source,
            int horizontal,
            int vertical,
            PointerPressedEventArgs e)
        {
            _window = window;
            _source = source;
            _horizontal = horizontal;
            _vertical = vertical;
            // Measure in screen pixels: dragging a west or north edge moves
            // the window origin, so window-relative coordinates would feed
            // the motion back into the next delta and halve the drag.
            _startScreenPointer = window.PointToScreen(e.GetPosition(window));
            _startPosition = window.Position;
            _startSize = window.ClientSize;
            source.PointerMoved += OnPointerMoved;
            source.PointerReleased += OnPointerReleased;
            source.PointerCaptureLost += OnPointerCaptureLost;
            e.Pointer.Capture(source);
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            // macOS screen points match Avalonia layout units one-to-one, so
            // the native deltas need no scaling conversion.
            var pointer = _window.PointToScreen(e.GetPosition(_window));
            var deltaX = pointer.X - _startScreenPointer.X;
            var deltaY = pointer.Y - _startScreenPointer.Y;
            var minWidth = Math.Max(_window.MinWidth, 0);
            var minHeight = Math.Max(_window.MinHeight, 0);
            var width = _startSize.Width;
            var height = _startSize.Height;

            if (_horizontal < 0) width = Math.Max(minWidth, width - deltaX);
            else if (_horizontal > 0) width = Math.Max(minWidth, width + deltaX);
            if (_vertical < 0) height = Math.Max(minHeight, height - deltaY);
            else if (_vertical > 0) height = Math.Max(minHeight, height + deltaY);

            var left = (double)_startPosition.X;
            var top = (double)_startPosition.Y;
            if (_horizontal < 0) left += _startSize.Width - width;
            if (_vertical < 0) top += _startSize.Height - height;

            _window.Width = width;
            _window.Height = height;
            var position = new PixelPoint(
                (int)Math.Round(left),
                (int)Math.Round(top));
            if (position != _window.Position) _window.Position = position;
            e.Handled = true;
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            e.Pointer.Capture(null);
            Finish();
            e.Handled = true;
        }

        private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => Finish();

        public void Dispose() => Finish();

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            _source.PointerMoved -= OnPointerMoved;
            _source.PointerReleased -= OnPointerReleased;
            _source.PointerCaptureLost -= OnPointerCaptureLost;
        }
    }
}

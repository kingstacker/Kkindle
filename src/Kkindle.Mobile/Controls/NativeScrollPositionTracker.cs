using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Kkindle.Mobile.Controls;

public interface INativeScrollPosition
{
    void UpdateScrollPosition();
}

internal sealed class NativeScrollPositionTracker(Control owner, Action update)
{
    private readonly List<ScrollViewer> _scrollers = [];
    private bool _attached;
    private bool _queued;

    public void Attach()
    {
        _attached = true;
        foreach (var scroller in owner.GetVisualAncestors().OfType<ScrollViewer>())
        {
            _scrollers.Add(scroller);
            scroller.ScrollChanged += OnScrollChanged;
            if (scroller is ElasticScrollViewer elastic)
                elastic.StretchChanged += OnStretchChanged;
        }
    }

    public void Detach()
    {
        _attached = false;
        foreach (var scroller in _scrollers)
        {
            scroller.ScrollChanged -= OnScrollChanged;
            if (scroller is ElasticScrollViewer elastic)
                elastic.StretchChanged -= OnStretchChanged;
        }
        _scrollers.Clear();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        update();
        QueueUpdate();
    }

    private void OnStretchChanged(object? sender, EventArgs e)
    {
        update();
        QueueUpdate();
    }

    private void QueueUpdate()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (_attached) update();
        }, DispatcherPriority.Render);
    }
}

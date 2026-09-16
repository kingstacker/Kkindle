using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Kkindle;

/// <summary>
/// A fixed-size, vertically scrolling wrap panel which realizes only the
/// rows intersecting the effective viewport (plus a small vertical cache).
///
/// Avalonia's stock <see cref="VirtualizingStackPanel"/> can virtualize one
/// axis, but the library needs a two-dimensional grid. Keeping the panel on
/// top of <see cref="VirtualizingPanel"/> means ListBox selection, item
/// containers and keyboard navigation continue to use the normal Avalonia
/// pipeline while the panel owns the grid math.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel
{
    private const double LayoutThreshold = 0.5;
    private const double DefaultViewportHeight = 768;

    private readonly Dictionary<int, Control> _realized = [];
    private readonly Dictionary<Control, int> _indices = [];
    private readonly HashSet<Control> _generatedContainers = [];

    private double _itemWidth = 166;
    private double _itemHeight = 304;
    private double _viewportWidth;
    private double _cacheLength = 0.5;
    private Orientation _orientation = Orientation.Horizontal;
    private Rect _effectiveViewport;
    private double _layoutWidth;
    private int _columns = 1;
    private int _pendingBringIntoViewIndex = -1;

    public VirtualizingWrapPanel()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    public Orientation Orientation
    {
        get => _orientation;
        set
        {
            if (_orientation == value) return;
            _orientation = value;
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    public double ItemWidth
    {
        get => _itemWidth;
        set
        {
            var width = NormalizeLength(value, _itemWidth);
            if (Math.Abs(_itemWidth - width) <= LayoutThreshold) return;
            _itemWidth = width;
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    public double ItemHeight
    {
        get => _itemHeight;
        set
        {
            var height = NormalizeLength(value, _itemHeight);
            if (Math.Abs(_itemHeight - height) <= LayoutThreshold) return;
            _itemHeight = height;
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    /// <summary>
    /// Width of the library viewport supplied by MainWindow. This is needed
    /// because the ListBox lives inside a ScrollViewer and can otherwise be
    /// measured with an infinite width during a refresh.
    /// </summary>
    public double ViewportWidth
    {
        get => _viewportWidth;
        set
        {
            var width = double.IsFinite(value) ? Math.Max(0, value) : 0;
            if (Math.Abs(_viewportWidth - width) <= LayoutThreshold) return;
            _viewportWidth = width;
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    /// <summary>
    /// Fraction of the visible height to keep realized above and below the
    /// viewport. 0.5 means half a viewport of overscan on each side.
    /// </summary>
    public double CacheLength
    {
        get => _cacheLength;
        set
        {
            var cache = double.IsFinite(value) ? Math.Max(0, value) : 0.5;
            if (Math.Abs(_cacheLength - cache) <= LayoutThreshold) return;
            _cacheLength = cache;
            InvalidateMeasure();
        }
    }

    public int RealizedCount => _realized.Count;

    public int FirstRealizedIndex => _realized.Count == 0 ? -1 : _realized.Keys.Min();

    public int LastRealizedIndex => _realized.Count == 0 ? -1 : _realized.Keys.Max();

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Orientation != Orientation.Horizontal)
            return base.MeasureOverride(availableSize);

        var itemWidth = Math.Max(1, ItemWidth);
        var itemHeight = Math.Max(1, ItemHeight);
        var width = ResolveLayoutWidth(availableSize.Width, itemWidth);
        _layoutWidth = width;
        _columns = CalculateColumnCount(width, itemWidth);

        var itemCount = Items.Count;
        var rowCount = CalculateRowCount(itemCount, _columns);
        var extentHeight = rowCount * itemHeight;
        var viewport = ResolveViewport(availableSize, width, extentHeight);
        var extendedViewport = ExtendViewport(viewport, extentHeight);

        UpdateRealizedRange(extendedViewport, itemCount, rowCount, _columns, itemHeight);
        foreach (var container in _realized.Values)
            container.Measure(new Size(itemWidth, itemHeight));

        return new Size(width, extentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Orientation != Orientation.Horizontal)
            return base.ArrangeOverride(finalSize);

        var itemWidth = Math.Max(1, ItemWidth);
        var itemHeight = Math.Max(1, ItemHeight);
        var width = _layoutWidth > 0 ? _layoutWidth : ResolveLayoutWidth(finalSize.Width, itemWidth);
        var columns = CalculateColumnCount(width, itemWidth);

        foreach (var pair in _realized)
        {
            var row = pair.Key / columns;
            var column = pair.Key % columns;
            pair.Value.Arrange(new Rect(
                column * itemWidth,
                row * itemHeight,
                itemWidth,
                itemHeight));
        }

        if (_pendingBringIntoViewIndex >= 0
            && _realized.TryGetValue(_pendingBringIntoViewIndex, out var pending))
        {
            _pendingBringIntoViewIndex = -1;
            pending.BringIntoView();
        }

        return finalSize;
    }

    protected override Control? ContainerFromIndex(int index) =>
        _realized.TryGetValue(index, out var container) ? container : null;

    protected override int IndexFromContainer(Control container) =>
        _indices.TryGetValue(container, out var index) ? index : -1;

    protected override IEnumerable<Control> GetRealizedContainers() =>
        _realized.OrderBy(pair => pair.Key).Select(pair => pair.Value);

    protected override IInputElement? GetControl(
        NavigationDirection direction,
        IInputElement? from,
        bool wrap)
    {
        var itemCount = Items.Count;
        if (itemCount == 0) return null;

        var fromIndex = FindIndex(from);
        var candidate = direction switch
        {
            NavigationDirection.Left or NavigationDirection.Previous => fromIndex - 1,
            NavigationDirection.Right or NavigationDirection.Next => fromIndex + 1,
            NavigationDirection.Up => fromIndex - _columns,
            NavigationDirection.Down => fromIndex + _columns,
            _ => fromIndex + 1
        };

        if (fromIndex < 0)
            candidate = direction is NavigationDirection.Left
                or NavigationDirection.Previous
                or NavigationDirection.Up
                ? itemCount - 1
                : 0;

        if (candidate < 0 || candidate >= itemCount)
        {
            if (!wrap) return null;
            candidate = candidate < 0 ? itemCount - 1 : 0;
        }

        return ScrollIntoView(candidate);
    }

    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count) return null;
        if (_realized.TryGetValue(index, out var realized))
        {
            realized.BringIntoView();
            return realized;
        }

        _pendingBringIntoViewIndex = index;
        var itemWidth = Math.Max(1, ItemWidth);
        var itemHeight = Math.Max(1, ItemHeight);
        var width = _layoutWidth > 0 ? _layoutWidth : ResolveLayoutWidth(Bounds.Width, itemWidth);
        var columns = CalculateColumnCount(width, itemWidth);
        var targetTop = index / columns * itemHeight;
        var viewer = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer is not null)
        {
            var maxOffset = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            var visibleHeight = viewer.Viewport.Height > 0 ? viewer.Viewport.Height : DefaultViewportHeight;
            var desiredTop = Math.Clamp(targetTop, 0, maxOffset);
            if (targetTop + itemHeight > desiredTop + visibleHeight)
                desiredTop = Math.Clamp(targetTop + itemHeight - visibleHeight, 0, maxOffset);
            viewer.Offset = new Vector(viewer.Offset.X, desiredTop);
        }

        InvalidateMeasure();
        return _realized.TryGetValue(index, out var realizedAfterScroll) ? realizedAfterScroll : null;
    }

    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        base.OnItemsControlChanged(oldValue);
        ClearRealized();
        InvalidateMeasure();
    }

    protected override void OnItemsChanged(
        IReadOnlyList<object?> items,
        NotifyCollectionChangedEventArgs e)
    {
        // Library refreshes replace the current page in one batch. Resetting
        // the small realized set is cheap and avoids stale index bookkeeping
        // for inserts, removes, replaces and moves alike.
        ClearRealized();
        _pendingBringIntoViewIndex = -1;
        InvalidateMeasure();
        InvalidateArrange();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClearRealized();
        _effectiveViewport = default;
        _pendingBringIntoViewIndex = -1;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (_effectiveViewport == e.EffectiveViewport) return;
        _effectiveViewport = e.EffectiveViewport;
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void UpdateRealizedRange(
        Rect extendedViewport,
        int itemCount,
        int rowCount,
        int columns,
        double itemHeight)
    {
        if (itemCount == 0 || rowCount == 0)
        {
            ClearRealized();
            return;
        }

        var firstRow = Math.Clamp((int)Math.Floor(extendedViewport.Top / itemHeight), 0, rowCount - 1);
        var lastRow = Math.Clamp(
            (int)Math.Ceiling(extendedViewport.Bottom / itemHeight) - 1,
            firstRow,
            rowCount - 1);
        var firstIndex = firstRow * columns;
        var lastIndex = Math.Min(itemCount - 1, ((lastRow + 1) * columns) - 1);

        foreach (var index in _realized.Keys
                     .Where(index => index < firstIndex || index > lastIndex)
                     .ToArray())
        {
            Unrealize(index);
        }

        for (var index = firstIndex; index <= lastIndex; index++)
            Realize(index);
    }

    private void Realize(int index)
    {
        if (_realized.ContainsKey(index)) return;

        var item = Items[index];
        var generator = ItemContainerGenerator;
        if (generator is null) return;
        var needsContainer = generator.NeedsContainer(item!, index, out var recycleKey);
        Control container;
        if (needsContainer)
        {
            container = generator.CreateContainer(item!, index, recycleKey!);
            _generatedContainers.Add(container);
        }
        else if (item is Control ownContainer)
        {
            container = ownContainer;
        }
        else
        {
            return;
        }

        generator.PrepareItemContainer(container, item!, index);
        AddInternalChild(container);
        _realized[index] = container;
        _indices[container] = index;
        generator.ItemContainerPrepared(container, item!, index);
    }

    private void Unrealize(int index)
    {
        if (!_realized.Remove(index, out var container)) return;
        _indices.Remove(container);

        RemoveInternalChild(container);
        if (_generatedContainers.Remove(container)
            && ItemContainerGenerator is { } generator)
        {
            generator.ClearItemContainer(container);
        }
    }

    private void ClearRealized()
    {
        foreach (var index in _realized.Keys.ToArray())
            Unrealize(index);
        _indices.Clear();
        _generatedContainers.Clear();
    }

    private int FindIndex(IInputElement? element)
    {
        for (var current = element as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Control control && _indices.TryGetValue(control, out var index))
                return index;
        }

        return -1;
    }

    private Rect ResolveViewport(Size availableSize, double width, double extentHeight)
    {
        if (extentHeight <= 0) return new Rect(0, 0, width, 0);

        var viewport = _effectiveViewport;
        var viewportHeight = viewport.Height;
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            viewportHeight = double.IsFinite(availableSize.Height) && availableSize.Height > 0
                ? availableSize.Height
                : DefaultViewportHeight;
            viewport = new Rect(0, 0, width, viewportHeight);
        }

        var top = double.IsFinite(viewport.Top) ? Math.Max(0, viewport.Top) : 0;
        top = Math.Min(top, Math.Max(0, extentHeight - viewportHeight));
        var height = Math.Min(viewportHeight, extentHeight);
        return new Rect(0, top, width, height);
    }

    private Rect ExtendViewport(Rect viewport, double extentHeight)
    {
        if (viewport.Height <= 0) return viewport;
        var cache = viewport.Height * Math.Max(0, CacheLength);
        var top = Math.Max(0, viewport.Top - cache);
        var bottom = Math.Min(extentHeight, viewport.Bottom + cache);
        return new Rect(0, top, viewport.Width, Math.Max(0, bottom - top));
    }

    private double ResolveLayoutWidth(double availableWidth, double itemWidth)
    {
        if (_viewportWidth > 0) return _viewportWidth;
        if (double.IsFinite(availableWidth) && availableWidth > 0) return availableWidth;
        if (Bounds.Width > 0) return Bounds.Width;
        return itemWidth * 4;
    }

    private static int CalculateColumnCount(double width, double itemWidth) =>
        Math.Max(1, (int)Math.Floor((width + LayoutThreshold) / itemWidth));

    private static int CalculateRowCount(int itemCount, int columns) =>
        itemCount <= 0 ? 0 : (int)Math.Ceiling(itemCount / (double)columns);

    private static double NormalizeLength(double value, double fallback)
    {
        return double.IsFinite(value) && value > 0 ? value : fallback;
    }
}

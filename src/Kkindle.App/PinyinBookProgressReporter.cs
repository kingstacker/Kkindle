using Avalonia.Threading;
using Kkindle.Core;

namespace Kkindle;

/// <summary>
/// Delivers local annotation progress in small batches, below input and render
/// priority. Completion must wait for FlushAsync so it cannot overtake rows.
/// </summary>
internal sealed class PinyinBookProgressReporter : IProgress<PinyinBookProgress>, IDisposable
{
    private const int BatchSize = 32;
    private readonly PinyinBookProgressWindow _window;
    private readonly object _gate = new();
    private readonly Queue<PinyinBookProgress> _pending = new();
    private TaskCompletionSource? _drained;
    private Exception? _deliveryError;
    private bool _scheduled;
    private bool _disposed;

    public PinyinBookProgressReporter(PinyinBookProgressWindow window)
    {
        _window = window;
        _window.Closed += OnWindowClosed;
    }

    public void Report(PinyinBookProgress value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_deliveryError is { } error)
                throw new InvalidOperationException("无法更新书籍注音进度。", error);
            _pending.Enqueue(value);
            if (_scheduled) return;
            _scheduled = true;
        }
        Dispatcher.UIThread.Post(DeliverBatch, DispatcherPriority.Background);
    }

    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (_deliveryError is { } error) return Task.FromException(error);
            if (!_scheduled || _disposed) return Task.CompletedTask;
            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    private void DeliverBatch()
    {
        var batch = new List<PinyinBookProgress>(BatchSize);
        lock (_gate)
        {
            if (_disposed) return;
            while (batch.Count < BatchSize && _pending.TryDequeue(out var value))
                batch.Add(value);
        }

        try
        {
            _window.UpdateBatch(batch);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _deliveryError = exception;
                _pending.Clear();
                _scheduled = false;
                _drained?.TrySetException(exception);
                _drained = null;
            }
            return;
        }

        lock (_gate)
        {
            if (_disposed) return;
            if (_pending.Count == 0)
            {
                _scheduled = false;
                _drained?.TrySetResult();
                _drained = null;
                return;
            }
        }
        Dispatcher.UIThread.Post(DeliverBatch, DispatcherPriority.Background);
    }

    private void OnWindowClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
            _scheduled = false;
            _drained?.TrySetResult();
            _drained = null;
        }
        _window.Closed -= OnWindowClosed;
    }
}

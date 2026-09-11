using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Limits the start of AI requests in a rolling one-minute window. A zero
/// limit disables throttling; the caller is responsible for passing a
/// normalized value when the setting comes from user input.
/// </summary>
internal sealed class AiRequestsPerMinuteLimiter : IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Queue<DateTimeOffset> _requestTimes = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public async Task WaitAsync(int requestsPerMinute, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(
            requestsPerMinute,
            0,
            BookTranslationSettings.MaxAiRequestsPerMinute);
        if (limit == 0) return;

        while (true)
        {
            TimeSpan delay;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                var now = DateTimeOffset.UtcNow;
                while (_requestTimes.Count > 0
                    && now - _requestTimes.Peek() >= Window)
                {
                    _requestTimes.Dequeue();
                }

                if (_requestTimes.Count < limit)
                {
                    _requestTimes.Enqueue(now);
                    return;
                }

                delay = Window - (now - _requestTimes.Peek());
            }
            finally
            {
                _gate.Release();
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}

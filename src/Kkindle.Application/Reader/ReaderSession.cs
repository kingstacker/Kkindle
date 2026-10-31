namespace Kkindle.ApplicationServices;

/// <summary>Owns the cancellation lifetime of the current reading session.</summary>
public sealed class ReaderSession : IDisposable
{
    private CancellationTokenSource? _current;

    public CancellationToken GetTokenOr(CancellationToken fallback) => _current?.Token ?? fallback;
    public bool IsCancellationRequested => _current?.IsCancellationRequested == true;

    public CancellationToken Begin(CancellationToken lifetime)
    {
        End();
        _current = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        return _current.Token;
    }

    public void Cancel() => _current?.Cancel();

    public void End()
    {
        var previous = _current;
        _current = null;
        if (previous is null) return;
        try { previous.Cancel(); }
        finally { previous.Dispose(); }
    }

    public void Dispose() => End();
}

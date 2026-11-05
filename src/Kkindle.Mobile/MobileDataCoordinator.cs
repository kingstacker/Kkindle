namespace Kkindle.Mobile;

/// <summary>
/// Process-wide barrier between mobile data operations and backup restoration.
/// Operations are declined while a restore is draining or after it succeeds.
/// A successful restore requires an application restart.
/// </summary>
public static class MobileDataCoordinator
{
    private static readonly object Sync = new();
    private static int _activeOperations;
    private static bool _restoring;
    private static bool _restoreCompleted;
    private static bool _recoveryIncomplete;
    private static TaskCompletionSource<bool>? _drained;

    /// <summary>True after a successful restore, until this process exits.</summary>
    public static bool IsRestartRequired
    {
        get { lock (Sync) return _restoreCompleted; }
    }

    /// <summary>True if startup must finish a failed import rollback.</summary>
    public static bool IsRecoveryIncomplete
    {
        get { lock (Sync) return _recoveryIncomplete; }
    }

    /// <summary>Starts an operation, or returns null if restoration blocks new work.</summary>
    public static OperationLease? TryBeginOperation()
    {
        lock (Sync)
        {
            if (_restoring || _restoreCompleted) return null;
            _activeOperations++;
            return new OperationLease();
        }
    }

    /// <summary>
    /// Blocks new operations immediately and waits for existing operations to finish.
    /// Returns null if another restore is in progress or already succeeded. Cancellation
    /// releases the barrier. Dispose the returned lease on every exit path.
    /// </summary>
    public static async Task<RestoreLease?> BeginRestoreAsync(CancellationToken cancellationToken = default)
    {
        Task wait;
        lock (Sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_restoring || _restoreCompleted) return null;
            _restoring = true;
            _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_activeOperations == 0) _drained.TrySetResult(true);
            wait = _drained.Task;
        }

        try
        {
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new RestoreLease();
        }
        catch
        {
            ReleaseRestore(completed: false);
            throw;
        }
    }

    private static void ReleaseRestore(bool completed, bool recoveryIncomplete = false)
    {
        lock (Sync)
        {
            if (completed)
            {
                _restoreCompleted = true;
                _recoveryIncomplete = recoveryIncomplete;
            }
            _restoring = false;
            _drained = null;
        }
    }

    public sealed class OperationLease : IDisposable
    {
        private int _disposed;
        internal OperationLease() { }
        internal bool IsActive => Volatile.Read(ref _disposed) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Sync)
            {
                if (--_activeOperations == 0) _drained?.TrySetResult(true);
            }
        }
    }

    public sealed class RestoreLease : IDisposable
    {
        private int _disposed;
        internal RestoreLease() { }

        /// <summary>Marks restoration successful and keeps the barrier closed until process exit.</summary>
        public void Complete(bool recoveryIncomplete = false)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                throw new ObjectDisposedException(nameof(RestoreLease));
            ReleaseRestore(completed: true, recoveryIncomplete);
        }

        /// <summary>Reopens the barrier if restoration did not succeed.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                ReleaseRestore(completed: false);
        }
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Tracks long-running work and stores only safe display metadata. Operation
/// delegates and cancellation sources remain in memory for the current process.
/// </summary>
public sealed class BackgroundTaskCenterService
{
    private const int MaximumHistoryEntries = 200;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex UrlPattern = new(@"(?i)\bhttps?://[^\s""'<>]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex WindowsPathPattern = new(@"(?:(?:[a-z]:\\|\\\\)[^\r\n\t""'<>|]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex UnixPathPattern = new(@"(?<![\w:])/(?:[^/\s]+/)+[^/\s]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex AuthorizationPattern = new(
        @"(?i)\bauthorization\b\s*[:=]\s*(?:bearer|basic)\s+[^\s,;]+|\b(?:bearer|basic)\s+[A-Za-z0-9._~+/-]+=*",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex SecretPattern = new(
        @"(?i)\b(?:api[-_ ]?key|access[-_ ]?key|secret|token|password|passwd|authorization|credential)\b\s*[:=]\s*[^\s,;]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly object _sync = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly string _historyPath;
    private readonly Dictionary<Guid, TaskState> _tasks = [];

    public BackgroundTaskCenterService(string historyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyPath);
        _historyPath = Path.GetFullPath(historyPath);
        LoadHistory();
    }

    public event EventHandler<BackgroundTaskChangedEventArgs>? Changed;

    public IReadOnlyList<BackgroundTaskSnapshot> GetSnapshot()
    {
        lock (_sync)
        {
            return _tasks.Values
                .OrderByDescending(task => task.CreatedAtUtc)
                .Select(ToSnapshot)
                .ToArray();
        }
    }

    /// <summary>
    /// Starts an operation on the caller's async context. CPU-bound callers
    /// should explicitly schedule their CPU work; UI-aware async operations can
    /// continue to use the UI dispatcher without being forced onto Task.Run.
    /// </summary>
    public BackgroundTaskHandle Start(
        string title,
        BackgroundTaskKind kind,
        BackgroundTaskOperation operation,
        CancellationToken cancellationToken = default,
        bool allowRetry = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(operation);

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TaskState state;
        int generation;
        lock (_sync)
        {
            state = new TaskState(id, SafeText(title, 160), kind, now)
            {
                Status = BackgroundTaskStatus.Running,
                UpdatedAtUtc = now,
                Operation = allowRetry ? operation : null,
                Cancellation = cancellation,
                RunGeneration = 1
            };
            generation = state.RunGeneration;
            _tasks.Add(id, state);
            TrimHistory();
        }

        RaiseChanged(id);
        var completion = ExecuteAsync(state, operation, cancellation, generation);
        return new BackgroundTaskHandle(id, completion);
    }

    public async Task<bool> RemoveCompletedAsync(Guid id)
    {
        lock (_sync)
        {
            if (!_tasks.TryGetValue(id, out var task)
                || task.Status is BackgroundTaskStatus.Running or BackgroundTaskStatus.Queued) return false;
            _tasks.Remove(id);
        }
        RaiseChanged(id);
        await PersistBestEffortAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Retries a failed or cancelled task from this process only.</summary>
    public BackgroundTaskHandle? Retry(Guid id, CancellationToken cancellationToken = default)
    {
        TaskState? state;
        BackgroundTaskOperation? operation;
        CancellationTokenSource? cancellation;
        int generation;
        lock (_sync)
        {
            if (!_tasks.TryGetValue(id, out state)
                || state.IsFromPreviousRun
                || state.Status is not (BackgroundTaskStatus.Failed or BackgroundTaskStatus.Canceled)
                || state.Operation is null)
            {
                return null;
            }

            // The state transition is inside the same lock as eligibility, so
            // simultaneous retry requests can only claim the task once.
            operation = state.Operation;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            state.Cancellation = cancellation;
            generation = ++state.RunGeneration;
            state.Status = BackgroundTaskStatus.Running;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            state.Stage = null;
            state.ProgressCurrent = null;
            state.ProgressTotal = null;
            state.Error = null;
        }

        RaiseChanged(id);
        var completion = ExecuteAsync(state, operation, cancellation, generation);
        return new BackgroundTaskHandle(id, completion);
    }

    /// <summary>Requests cancellation for a running task; completion remains owned by its operation.</summary>
    public bool Cancel(Guid id)
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (!_tasks.TryGetValue(id, out var state) || state.Status != BackgroundTaskStatus.Running)
                return false;
            cancellation = state.Cancellation;
        }

        if (cancellation is null) return false;
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task ExecuteAsync(TaskState state, BackgroundTaskOperation operation, CancellationTokenSource cancellation, int generation)
    {
        // Start persistence in parallel, then invoke the operation immediately
        // on the caller's context. A crash still restores Running as Interrupted.
        _ = PersistBestEffortAsync();

        Exception? failure = null;
        var canceled = false;
        try
        {
            var context = new BackgroundTaskContext((stage, current, total) =>
                ReportProgress(state, generation, cancellation, stage, current, total), titles =>
                {
                    lock (_sync)
                    {
                        if (state.Status != BackgroundTaskStatus.Running || state.RunGeneration != generation
                            || !ReferenceEquals(state.Cancellation, cancellation)) return;
                        state.BookTitles = titles.Select(title => SafeText(title, 300))
                            .Where(title => !string.IsNullOrWhiteSpace(title)).Select(title => title!)
                            .Distinct(StringComparer.Ordinal).ToArray();
                    }
                    RaiseChanged(state.Id);
                });
            await operation(context, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            canceled = true;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        lock (_sync)
        {
            if (state.RunGeneration != generation || !ReferenceEquals(state.Cancellation, cancellation))
            {
                cancellation.Dispose();
                return;
            }
            // A late cancel request cannot turn an operation that returned
            // normally into a cancelled task.
            state.Status = failure is not null
                ? BackgroundTaskStatus.Failed
                : canceled
                    ? BackgroundTaskStatus.Canceled
                    : BackgroundTaskStatus.Succeeded;
            state.Error = failure is null ? null : SafeText(failure.Message, 500);
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            state.Cancellation = null;
        }

        cancellation.Dispose();
        RaiseChanged(state.Id);
        await PersistBestEffortAsync().ConfigureAwait(false);
    }

    private void ReportProgress(TaskState state, int generation, CancellationTokenSource cancellation, string? stage, int? current, int? total)
    {
        var changed = false;
        lock (_sync)
        {
            if (state.Status != BackgroundTaskStatus.Running
                || state.RunGeneration != generation
                || !ReferenceEquals(state.Cancellation, cancellation)) return;
            var cleanStage = string.IsNullOrWhiteSpace(stage) ? null : SafeText(stage, 120);
            var safeTotal = total is < 0 ? 0 : total;
            var safeCurrent = current is < 0 ? 0 : current;
            if (safeTotal.HasValue && safeCurrent.HasValue)
                safeCurrent = Math.Min(safeCurrent.Value, safeTotal.Value);

            if (state.Stage != cleanStage || state.ProgressCurrent != safeCurrent || state.ProgressTotal != safeTotal)
            {
                state.Stage = cleanStage;
                state.ProgressCurrent = safeCurrent;
                state.ProgressTotal = safeTotal;
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;
                changed = true;
            }
        }
        if (changed) RaiseChanged(state.Id);
    }

    private async Task PersistBestEffortAsync()
    {
        string? temporaryPath = null;
        try
        {
            await _persistGate.WaitAsync().ConfigureAwait(false);
            try
            {
                PersistedDocument document;
                lock (_sync)
                {
                    document = new PersistedDocument(
                        1,
                        _tasks.Values
                            .OrderByDescending(task => task.CreatedAtUtc)
                            .Take(MaximumHistoryEntries)
                            .Select(ToPersisted)
                            .ToArray());
                }

                var directory = Path.GetDirectoryName(_historyPath);
                if (string.IsNullOrWhiteSpace(directory)) return;
                Directory.CreateDirectory(directory);
                temporaryPath = _historyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await JsonSerializer.SerializeAsync(stream, document, JsonOptions).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                File.Move(temporaryPath, _historyPath, overwrite: true);
                temporaryPath = null;
            }
            finally
            {
                _persistGate.Release();
            }
        }
        catch
        {
            // History storage is best-effort and must never fail the book task.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch { /* Keep the task outcome even if cleanup is unavailable. */ }
            }
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyPath)) return;
            using var stream = File.OpenRead(_historyPath);
            var document = JsonSerializer.Deserialize<PersistedDocument>(stream, JsonOptions);
            if (document?.Tasks is null || document.Version != 1) return;
            foreach (var entry in document.Tasks.Take(MaximumHistoryEntries))
            {
                if (entry.Id == Guid.Empty || !Enum.IsDefined(entry.Kind) || !Enum.IsDefined(entry.Status)) continue;
                var interrupted = entry.Status is BackgroundTaskStatus.Running or BackgroundTaskStatus.Queued;
                var state = new TaskState(entry.Id, SafeText(entry.Title, 160), entry.Kind, entry.CreatedAtUtc)
                {
                    Status = interrupted ? BackgroundTaskStatus.Interrupted : entry.Status,
                    UpdatedAtUtc = entry.UpdatedAtUtc,
                    Stage = SafeText(entry.Stage, 120),
                    BookTitles = entry.BookTitles?.Select(title => SafeText(title, 300))
                        .Where(title => !string.IsNullOrWhiteSpace(title)).Select(title => title!).ToArray(),
                    ProgressCurrent = entry.ProgressCurrent,
                    ProgressTotal = entry.ProgressTotal,
                    Error = interrupted
                        ? "应用关闭时任务尚未完成。"
                        : SafeText(entry.Error, 500),
                    IsFromPreviousRun = true
                };
                _tasks[state.Id] = state;
            }
        }
        catch
        {
            // A missing or damaged history file should not prevent startup.
        }
    }

    private static BackgroundTaskSnapshot ToSnapshot(TaskState state) => new(
        state.Id,
        state.Title,
        state.Kind,
        state.Status,
        state.CreatedAtUtc,
        state.UpdatedAtUtc,
        state.Stage,
        state.ProgressCurrent,
        state.ProgressTotal,
        state.Error,
        state.Status == BackgroundTaskStatus.Running && state.Cancellation is not null,
        !state.IsFromPreviousRun
            && state.Operation is not null
            && state.Status is BackgroundTaskStatus.Failed or BackgroundTaskStatus.Canceled,
        state.IsFromPreviousRun,
        state.BookTitles);

    private static PersistedTask ToPersisted(TaskState state) => new(
        state.Id,
        SafeText(state.Title, 160),
        state.Kind,
        state.Status,
        state.CreatedAtUtc,
        state.UpdatedAtUtc,
        SafeText(state.Stage, 120),
        state.ProgressCurrent,
        state.ProgressTotal,
        SafeText(state.Error, 500),
        state.BookTitles);

    private void TrimHistory()
    {
        var completed = _tasks.Values
            .Where(task => task.Status != BackgroundTaskStatus.Running)
            .OrderBy(task => task.CreatedAtUtc)
            .ToArray();
        var removeCount = Math.Max(0, _tasks.Count - MaximumHistoryEntries);
        foreach (var task in completed.Take(removeCount)) _tasks.Remove(task.Id);
    }

    private static string? SafeText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ').Trim();
        if (cleaned.Length > maximumLength) cleaned = cleaned[..maximumLength];
        try
        {
            cleaned = AuthorizationPattern.Replace(cleaned, "授权信息=[已隐藏]");
            cleaned = SecretPattern.Replace(cleaned, "凭据=[已隐藏]");
            cleaned = UrlPattern.Replace(cleaned, "[链接]");
            cleaned = WindowsPathPattern.Replace(cleaned, "[路径]");
            cleaned = UnixPathPattern.Replace(cleaned, "[路径]");
            return cleaned.Length <= maximumLength ? cleaned : cleaned[..maximumLength];
        }
        catch (RegexMatchTimeoutException)
        {
            return "详细错误信息已隐藏。";
        }
    }

    private void RaiseChanged(Guid id)
    {
        try { Changed?.Invoke(this, new BackgroundTaskChangedEventArgs(id)); }
        catch { /* A UI subscriber must not break operation tracking. */ }
    }

    private sealed class TaskState(Guid id, string? title, BackgroundTaskKind kind, DateTimeOffset createdAtUtc)
    {
        public Guid Id { get; } = id;
        public string Title { get; } = title ?? "后台任务";
        public BackgroundTaskKind Kind { get; } = kind;
        public BackgroundTaskStatus Status { get; set; } = BackgroundTaskStatus.Queued;
        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
        public DateTimeOffset UpdatedAtUtc { get; set; } = createdAtUtc;
        public string? Stage { get; set; }
        public string[]? BookTitles { get; set; }
        public int? ProgressCurrent { get; set; }
        public int? ProgressTotal { get; set; }
        public string? Error { get; set; }
        public bool IsFromPreviousRun { get; set; }
        public BackgroundTaskOperation? Operation { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public int RunGeneration { get; set; }
    }

    private sealed record PersistedDocument(int Version, PersistedTask[] Tasks);

    private sealed record PersistedTask(
        Guid Id,
        string? Title,
        BackgroundTaskKind Kind,
        BackgroundTaskStatus Status,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        string? Stage,
        int? ProgressCurrent,
        int? ProgressTotal,
        string? Error,
        string[]? BookTitles = null);
}

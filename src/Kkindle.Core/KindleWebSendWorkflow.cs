namespace Kkindle.Core;

public sealed record KindleWebPageFile(string Name, string Status);

public sealed record KindleWebRecentFile
{
    public string Sent { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}

public sealed record KindleWebPageSnapshot
{
    public string Page { get; init; } = "loading";
    public string Account { get; init; } = string.Empty;
    public string[] ReadyNames { get; init; } = [];
    public KindleWebPageFile[] Files { get; init; } = [];
    public KindleWebRecentFile[] RecentFiles { get; init; } = [];
    public double Percentage { get; init; }
    public bool HasError { get; init; }
    public KindleWebAuthenticationSnapshot? Authentication { get; init; }
}

public interface IKindleWebPage
{
    Task ResetAsync(CancellationToken cancellationToken);
    Task<KindleWebPageSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task StageAsync(IReadOnlyList<KindleWebFile> files, CancellationToken cancellationToken);
    Task<bool> SubmitAsync(CancellationToken cancellationToken);
}

/// <summary>One explicit Send click owns one immutable batch. Never retries a submission.</summary>
public sealed class KindleWebSendWorkflow(IKindleWebPage page)
{
    public bool SubmissionAttempted { get; private set; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan PageTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan UploadTimeout { get; init; } = TimeSpan.FromMinutes(20);

    public async Task<KindleWebPageFile[]> SendAsync(IReadOnlyList<KindleWebFile> files,
        Action<string, KindleWebPageSnapshot?> report, CancellationToken cancellationToken)
    {
        if (files.Count == 0) throw new ArgumentException("The batch is empty.", nameof(files));
        var names = files.Select(file => file.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException("Send identically named files in separate batches.", nameof(files));

        report("preparing", null);
        await page.ResetAsync(cancellationToken);
        await WaitUntilReadyAsync(names: null, report, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await page.StageAsync(files, cancellationToken);
        await WaitUntilReadyAsync(names, report, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // A timeout/disconnect during this call is ambiguous. Mark the attempt
        // before awaiting so callers cannot offer a blind automatic retry.
        SubmissionAttempted = true;
        if (!await page.SubmitAsync(cancellationToken))
        {
            SubmissionAttempted = false;
            throw new InvalidOperationException(UiText.Get("网页未接受文件列表，请打开网页检查后重试。"));
        }

        var latest = names.ToDictionary(name => name, _ => "unknown", StringComparer.Ordinal);
        var deadline = DateTimeOffset.UtcNow + UploadTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await page.ReadAsync(cancellationToken);
            foreach (var file in snapshot.Files)
                if (latest.ContainsKey(file.Name) && file.Status is "submitted" or "failed")
                    latest[file.Name] = file.Status;
            report("sending", snapshot);
            if (latest.Values.All(status => status is "submitted" or "failed")) break;
            if (snapshot.HasError || snapshot.Page is "signin" or "unknown") break;
            await Task.Delay(PollInterval, cancellationToken);
        }
        return names.Select(name => new KindleWebPageFile(name, latest[name])).ToArray();
    }

    private async Task WaitUntilReadyAsync(string[]? names,
        Action<string, KindleWebPageSnapshot?> report, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + PageTimeout;
        DateTimeOffset? signInDeadline = null;
        while (DateTimeOffset.UtcNow < (signInDeadline ?? deadline))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await page.ReadAsync(cancellationToken);
            report(snapshot.Page == "signin" ? "signin" : "preparing", snapshot);
            if (snapshot.Page == "signin")
            {
                signInDeadline ??= DateTimeOffset.UtcNow + SignInTimeout;
                deadline = DateTimeOffset.UtcNow + PageTimeout;
            }
            else
            {
                signInDeadline = null;
                if (snapshot.HasError)
                    throw new InvalidOperationException(UiText.Get("网页需要处理提示，请打开网页检查。"));
                if (snapshot.Page == "ready" && (names is null
                        ? snapshot.ReadyNames.Length == 0
                        : snapshot.ReadyNames.Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal))))
                    return;
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
        throw new TimeoutException(UiText.Get("等待网页超时，请检查网络或打开网页重试。"));
    }
}

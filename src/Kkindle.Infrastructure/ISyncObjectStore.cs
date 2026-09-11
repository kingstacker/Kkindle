namespace Kkindle.Infrastructure;

/// <summary>
/// The snapshot/merge protocol depends only on these object operations.
/// Keys are relative to the configured remote root; uploads leave input open.
/// </summary>
internal interface ISyncObjectStore : IDisposable
{
    Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);
    Task<SyncObjectRead> OpenReadAsync(string key, CancellationToken cancellationToken);
    Task PutAsync(string key, Stream input, string contentType, CancellationToken cancellationToken);
    Task TestWriteAsync(string prefix, CancellationToken cancellationToken);
}

internal sealed class SyncObjectRead(Stream stream, long contentLength, IDisposable owner) : IDisposable
{
    public Stream ResponseStream { get; } = stream;
    public long ContentLength { get; } = contentLength;
    public void Dispose() => owner.Dispose();
}

internal sealed class SyncObjectNotFoundException(string message, Exception? inner = null)
    : IOException(message, inner);

internal sealed class SyncListingUnavailableException(string message, Exception inner)
    : IOException(message, inner);

namespace Kkindle.Core;

/// <summary>Platform bridge for a user-approved batch in the official upload page.</summary>
public interface IKindleWebFileInput
{
    Task SetFilesAsync(nint webViewHandle, string elementId, IReadOnlyList<string> paths,
        CancellationToken cancellationToken);
}

/// <summary>Optional platform settings for the dedicated, persistent Amazon session.</summary>
public interface IKindleWebBrowserSettings
{
    bool DisableCredentialSaving(nint webViewHandle);
}

using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile;

/// <summary>
/// Picks files for import. Desktop shows an open dialog; Android copies the
/// picked document-provider URIs into app-private storage before returning.
/// </summary>
public interface IFileImportSource
{
    Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Releases temporary files created by the picker after import.</summary>
    void ReleaseFiles(IReadOnlyList<string> files) { }
}

/// <summary>Shares a complete file using the platform's system interface.</summary>
public interface IMobileFileShare
{
    Task<bool> ShareAsync(string fileName, byte[] content);

    /// <summary>Shares an existing file; the caller retains it until the task completes.</summary>
    Task<bool> ShareFileAsync(string path);
}

/// <summary>Allows a platform to finish a short save when its app is backgrounded.</summary>
public interface IMobileBackgroundWork
{
    void Run(Func<Task> work);
}

/// <summary>
/// Picks system bar icon colors. Avalonia follows the app theme only, but the
/// reader's paper can be black while the app is light.
/// </summary>
public interface ISystemBarAppearance
{
    void SetIconsForBackground(bool darkBackground);

    /// <summary>
    /// Keeps edge drags inside the app instead of Android's back gesture, so a
    /// selection that starts at the margin is not mistaken for "go back".
    /// The reader turns it on; tab pages keep the system gesture.
    /// </summary>
    void SetEdgeGesturesCaptured(bool captured);
}

/// <summary>Platform adapters a mobile head must supply before Avalonia starts.</summary>
/// <param name="Paths">App-private data root. Android passes its files directory.</param>
/// <param name="SecretProtector">OS-bound encryption for API keys and SMTP passwords.</param>
/// <param name="MetadataService">Reads EPUB/PDF metadata during import.</param>
/// <param name="FormatConverter">
/// Formats the platform cannot convert (for example AZW3 without Calibre) use
/// <see cref="Kkindle.ApplicationServices.UnavailableBookFormatConverter"/>.
/// </param>
/// <param name="ImportSource">Document picker adapter.</param>
/// <param name="SystemBars">Absent on hosts without system bars.</param>
/// <param name="CreateTtsEngine">Speech for reading aloud; absent where none exists.</param>
/// <param name="CreateTtsPlayer">Audio output paired with <paramref name="CreateTtsEngine"/>.</param>
/// <param name="TabBar">
/// Native tab bar for hosts that can draw a better one than Avalonia (iOS 26
/// Liquid Glass). The shell keeps its own bar as an invisible layout spacer.
/// </param>
public sealed record MobileServices(
    AppPaths Paths,
    ISecretProtector SecretProtector,
    IMetadataService MetadataService,
    IBookFormatConverter FormatConverter,
    IFileImportSource ImportSource,
    ISystemBarAppearance? SystemBars = null,
    Func<ITtsEngine>? CreateTtsEngine = null,
    Func<ITtsAudioPlayer>? CreateTtsPlayer = null,
    IMobileTabBar? TabBar = null,
    IMobileBackgroundWork? BackgroundWork = null,
    IMobileFileShare? FileShare = null);

/// <summary>Fallback for hosts without a document picker (designer, preview).</summary>
public sealed class NoFileImportSource : IFileImportSource
{
    public Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

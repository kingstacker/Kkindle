using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>Platform adapters; business services belong to ApplicationModules.</summary>
/// <param name="ReaderHostFactory">
/// Legacy WebView adapter hook, retained for existing desktop heads. Native
/// format-aware rendering uses FormatReaderHostFactory instead.
/// </param>
/// <param name="FormatReaderHostFactory">Optional EPUB/PDF rendering adapter.</param>
/// <param name="TtsCacheDirectory">Optional platform-selected audio cache directory.</param>
/// <param name="PdfDocumentFactory">Optional backend shared by PDF import and the default PDF reader.</param>
/// <param name="SyncService">Optional caller-owned synchronization adapter.</param>
public sealed record AppServices(
    ISecretProtector SecretProtector,
    Func<IntPtr, IDeviceChangeNotifier?> CreateDeviceChangeNotifier,
    IKindleDeviceService? KindleDeviceService = null,
    Func<IReaderHost>? ReaderHostFactory = null,
    AppPaths? Paths = null,
    string? RootConfigurationDirectory = null,
    IAppUpdateInstaller? UpdateInstaller = null,
    ITtsEngine? TtsEngine = null,
    ITtsAudioPlayer? TtsAudioPlayer = null,
    ITtsEnvironmentSetup? TtsEnvironmentSetup = null,
    IKindleWebFileInput? KindleWebFileInput = null,
    IReaderHostFactory? FormatReaderHostFactory = null,
    string? TtsCacheDirectory = null,
    IPdfDocumentFactory? PdfDocumentFactory = null,
    ISyncService? SyncService = null);

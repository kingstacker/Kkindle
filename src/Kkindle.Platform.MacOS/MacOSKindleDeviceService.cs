using Kkindle.Core;
using Kkindle.Platform.Common;

namespace Kkindle.Platform.MacOS;

/// <summary>
/// macOS exposes Kindle devices in two ways: older models mount as USB mass
/// storage, newer models (Kindle Scribe and most 2016+ devices) only speak MTP.
/// This service merges both transports behind the shared device interface.
/// </summary>
public sealed class MacOSKindleDeviceService : IKindleDeviceService
{
    private readonly MassStorageKindleDeviceService _massStorage;
    private readonly MtpKindleDeviceService _mtp;

    public MacOSKindleDeviceService(
        MassStorageKindleDeviceService massStorage,
        MtpKindleDeviceService mtp)
    {
        _massStorage = massStorage ?? throw new ArgumentNullException(nameof(massStorage));
        _mtp = mtp ?? throw new ArgumentNullException(nameof(mtp));
    }

    public async Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default)
    {
        var massStorage = await _massStorage.DetectDevicesAsync(cancellationToken).ConfigureAwait(false);
        var mtp = await _mtp.DetectDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (mtp.Count == 0) return massStorage;
        return [.. massStorage, .. mtp];
    }

    public Task<IReadOnlyList<KindleBook>> GetCachedBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
        Service(device).GetCachedBooksAsync(device, cancellationToken);

    public Task<IReadOnlyList<KindleBook>> ScanBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
        Service(device).ScanBooksAsync(device, cancellationToken);

    public Task<IReadOnlyList<KindleBook>> ScanBooksProgressivelyAsync(
        KindleDevice device,
        IProgress<KindleScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Service(device).ScanBooksProgressivelyAsync(device, progress, cancellationToken);

    public Task SendBookAsync(
        KindleDevice device,
        BookFile bookFile,
        string sourcePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? coverOverridePath = null) =>
        Service(device).SendBookAsync(device, bookFile, sourcePath, progress, cancellationToken, coverOverridePath);

    public Task RemoveBookAsync(KindleDevice device, KindleBook book, CancellationToken cancellationToken = default) =>
        Service(device).RemoveBookAsync(device, book, cancellationToken);

    public Task<string> ExportBookAsync(
        KindleDevice device,
        KindleBook book,
        string destinationDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Service(device).ExportBookAsync(device, book, destinationDirectory, progress, cancellationToken);

    public Task<IReadOnlyList<KindleDeviceResource>> ScanResourcesAsync(
        KindleDevice device,
        KindleResourceKind kind,
        CancellationToken cancellationToken = default) =>
        Service(device).ScanResourcesAsync(device, kind, cancellationToken);

    public Task SendResourceAsync(
        KindleDevice device,
        KindleResourceKind kind,
        string sourcePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Service(device).SendResourceAsync(device, kind, sourcePath, progress, cancellationToken);

    public Task ExportResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        string destinationPath,
        CancellationToken cancellationToken = default) =>
        Service(device).ExportResourceAsync(device, resource, destinationPath, cancellationToken);

    public Task RemoveResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        CancellationToken cancellationToken = default) =>
        Service(device).RemoveResourceAsync(device, resource, cancellationToken);

    public Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default,
        int maxItems = int.MaxValue) =>
        Service(device).ReadClippingsAsync(device, cancellationToken, maxItems);

    public Task DeleteClippingAsync(KindleDevice device, string clippingId, CancellationToken cancellationToken = default) =>
        Service(device).DeleteClippingAsync(device, clippingId, cancellationToken);

    public Task DeleteClippingsAsync(
        KindleDevice device,
        IReadOnlyCollection<string> clippingIds,
        CancellationToken cancellationToken = default) =>
        Service(device).DeleteClippingsAsync(device, clippingIds, cancellationToken);

    public Task<KindleAnnotationSnapshot?> ReadAnnotationSnapshotAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default) =>
        Service(device).ReadAnnotationSnapshotAsync(device, cancellationToken);

    public Task<KindleAnnotationWriteResult> ApplyAnnotationMutationsAsync(
        KindleDevice device,
        KindleAnnotationSnapshot snapshot,
        IReadOnlyList<KindleAnnotationMutation> mutations,
        CancellationToken cancellationToken = default) =>
        Service(device).ApplyAnnotationMutationsAsync(device, snapshot, mutations, cancellationToken);

    public Task<KindleAnnotationWriteResult> ApplyAnnotationMutationsAsync(
        KindleDevice device,
        KindleAnnotationSnapshot snapshot,
        IReadOnlyList<KindleAnnotationMutation> mutations,
        CancellationToken cancellationToken,
        IProgress<KindleAnnotationSyncProgress>? progress) =>
        Service(device).ApplyAnnotationMutationsAsync(device, snapshot, mutations, cancellationToken, progress);

    public Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
        Service(device).EjectAsync(device, cancellationToken);

    private IKindleDeviceService Service(KindleDevice device) =>
        device.Transport == KindleTransport.Mtp ? _mtp : _massStorage;
}

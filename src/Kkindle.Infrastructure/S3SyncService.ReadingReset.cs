namespace Kkindle.Infrastructure;

public sealed partial class S3SyncService
{
    private static bool IsResetReadingEntity(string entityType) =>
        NormalizeTombstoneEntityType(entityType) is "progress" or "stats";

    private static IEnumerable<S3SyncTombstone> FilterReadingTombstones(
        IEnumerable<S3SyncTombstone> tombstones, ReadingDataReset? reset) => tombstones
        .Where(item => !IsResetReadingEntity(item.EntityType) || item.ReadingDataResetId == reset?.Id);
}

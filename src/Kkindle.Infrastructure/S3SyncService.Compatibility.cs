using Kkindle.Core;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kkindle.Infrastructure;

public sealed partial class S3SyncService
{
    private static void ValidateSnapshotCompatibility(S3SyncSnapshot snapshot)
    {
        var minimum = snapshot.MinimumReaderVersion ?? snapshot.Version;
        if (snapshot.Version < 1 || minimum < 1 || minimum > snapshot.Version)
            throw new InvalidDataException("Invalid sync snapshot compatibility declaration.");
        if (minimum > SnapshotVersion)
            throw new InvalidDataException(UiText.Get(
                "同步快照版本 {0} 高于当前版本。请先升级 Kkindle。", minimum));
    }

    private static void PreserveForwardCompatibleData(S3SyncSnapshot target,
        S3SyncSnapshot? baseline, IReadOnlyList<S3SyncSnapshot> remotes)
    {
        var sources = remotes.Concat(baseline is null ? [] : new[] { baseline })
            .OrderBy(snapshot => snapshot.CreatedAt)
            .ThenBy(snapshot => snapshot.DeviceId, StringComparer.Ordinal).ToArray();
        foreach (var source in sources)
        {
            MergeOpaqueFields(target, source, source.CreatedAt);
            if (target.ReadingDataReset == source.ReadingDataReset)
                MergeOpaqueFields(target.ReadingDataReset, source.ReadingDataReset, source.CreatedAt, target, source, "ReadingDataReset/");
        }

        // Only surviving rows receive opaque fields. Never resurrect deleted
        // entities merely to carry data this reader does not understand.
        foreach (var entity in SyncEntities)
        {
            var sourceRows = sources.SelectMany(entity.Rows)
                .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => group.OrderBy(row => row.Version).ToArray(), StringComparer.OrdinalIgnoreCase);
            foreach (var row in entity.Rows(target))
                if (sourceRows.TryGetValue(row.Key, out var matches))
                    foreach (var source in matches)
                        MergeOpaqueFields(row.Value, source.Value, source.Version);
        }
        var tombstones = sources.SelectMany(snapshot => snapshot.Tombstones)
            .GroupBy(row => VersionKey(row.EntityType, row.Key))
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.DeletedAt).ToArray());
        foreach (var row in target.Tombstones)
            if (tombstones.TryGetValue(VersionKey(row.EntityType, row.Key), out var matches))
                foreach (var source in matches) MergeOpaqueFields(row, source, source.DeletedAt);

        foreach (var source in sources.Where(snapshot => snapshot.Settings is not null))
        {
            var settings = source.Settings!;
            MergeOpaqueFields(target.Settings, settings, settings.UpdatedAt);
            MergeOpaqueFields(target.Settings?.App, settings.App, settings.AppUpdatedAt ?? settings.UpdatedAt);
            MergeOpaqueFields(target.Settings?.Ai, settings.Ai, settings.AiUpdatedAt ?? settings.UpdatedAt);
            MergeOpaqueFields(target.Settings?.KindleEmail, settings.KindleEmail,
                settings.KindleEmailUpdatedAt ?? settings.UpdatedAt);
        }
        // This reader republishes its own wire version, not a version whose
        // features it cannot implement. Unknown fields remain explicitly opaque.
        target.Version = SnapshotVersion;
        target.MinimumReaderVersion = SnapshotVersion;
    }

    private static void MergeOpaqueFields(object? target, object? source, DateTimeOffset version,
        S3SyncExtensibleData? targetClock = null, S3SyncExtensibleData? sourceClock = null, string prefix = "")
    {
        if (target is null || source is null) return;
        var property = ExtensionProperty(source.GetType());
        if (property?.GetValue(source) is Dictionary<string, JsonElement> fields && fields.Count > 0)
        {
            var destination = property.GetValue(target) as Dictionary<string, JsonElement>;
            if (destination is null)
            {
                destination = new(StringComparer.Ordinal);
                property.SetValue(target, destination);
            }
            var targetData = targetClock ?? target as S3SyncExtensibleData;
            var sourceData = sourceClock ?? source as S3SyncExtensibleData;
            if (targetData is not null) targetData.ExtensionDataVersions ??= new(StringComparer.Ordinal);
            foreach (var (name, value) in fields)
            {
                var clockKey = prefix + name;
                var fieldVersion = sourceData?.ExtensionDataVersions?.TryGetValue(clockKey, out var stored) == true ? stored : version;
                if (targetData?.ExtensionDataVersions?.TryGetValue(clockKey, out var current) == true
                    && (current > fieldVersion || current == fieldVersion
                        && destination.TryGetValue(name, out var existing)
                        && string.CompareOrdinal(existing.GetRawText(), value.GetRawText()) >= 0)) continue;
                destination[name] = value.Clone();
                if (targetData is not null) targetData.ExtensionDataVersions![clockKey] = fieldVersion;
            }
        }
        // DefaultReaderLayout is a shared Core type, not a snapshot DTO.
        if (target is S3SyncAppSettings targetApp && source is S3SyncAppSettings sourceApp)
            MergeOpaqueFields(targetApp.DefaultReaderLayout, sourceApp.DefaultReaderLayout, version,
                targetApp, sourceApp, "DefaultReaderLayout/");
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo[]> ExtensionProperties = new();
    private static PropertyInfo? ExtensionProperty(Type type) => ExtensionProperties.GetOrAdd(type,
        static value => value.GetProperties().Where(property =>
            property.GetCustomAttribute<JsonExtensionDataAttribute>() is not null).ToArray()).FirstOrDefault();

    private static bool HasForwardCompatibleData(S3SyncSnapshot snapshot)
    {
        if (snapshot.Version > SnapshotVersion || HasOpaqueFields(snapshot)
            || HasOpaqueFields(snapshot.ReadingDataReset) || HasOpaqueFields(snapshot.Settings)
            || HasOpaqueFields(snapshot.Settings?.App) || HasOpaqueFields(snapshot.Settings?.App?.DefaultReaderLayout)
            || HasOpaqueFields(snapshot.Settings?.Ai) || HasOpaqueFields(snapshot.Settings?.KindleEmail)) return true;
        return SyncEntities.Any(entity => entity.Rows(snapshot).Any(row => HasOpaqueFields(row.Value)))
            || snapshot.Tombstones.Any(HasOpaqueFields);
    }

    private static bool HasOpaqueFields(object? value) => value is not null
        && ExtensionProperty(value.GetType())?.GetValue(value) is Dictionary<string, JsonElement> { Count: > 0 };
}

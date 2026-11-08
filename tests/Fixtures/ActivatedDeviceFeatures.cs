using Kkindle.Core;

namespace Kkindle.TestFixtures;

// An explicitly licensed fictional device for existing transport/format tests.
internal sealed class ActivatedDeviceFeatures : IDeviceFeatureAccess
{
    public Task EnsureFeatureAsync(DeviceFeature feature, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

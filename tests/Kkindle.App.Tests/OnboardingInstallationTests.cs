using Xunit;

namespace Kkindle.Ui.Tests;

public sealed class OnboardingInstallationTests
{
    [Fact]
    public async Task RunsOnlySelectedInstallationsAndRetriesOnlyFailures()
    {
        var coordinator = new OnboardingInstallationCoordinator();
        var calibreCalls = 0;
        var ttsCalls = 0;
        Task<bool> Calibre() => Task.FromResult(++calibreCalls > 1);
        Task<bool> Tts() { ttsCalls++; return Task.FromResult(true); }

        Assert.False(await coordinator.PrepareAsync(true, true, Calibre, Tts));
        Assert.Equal(1, calibreCalls);
        Assert.Equal(1, ttsCalls);
        Assert.True(await coordinator.PrepareAsync(true, true, Calibre, Tts));
        Assert.Equal(2, calibreCalls);
        Assert.Equal(1, ttsCalls);

        var skipped = new OnboardingInstallationCoordinator();
        Assert.True(await skipped.PrepareAsync(false, false, Calibre, Tts));
        Assert.Equal(2, calibreCalls);
        Assert.Equal(1, ttsCalls);
    }

    [Fact]
    public async Task ClearingAFailedSelectionAllowsContinuing()
    {
        var coordinator = new OnboardingInstallationCoordinator();
        Task<bool> Fail() => Task.FromResult(false);
        Assert.False(await coordinator.PrepareAsync(true, false, Fail, Fail));
        Assert.True(await coordinator.PrepareAsync(false, false, Fail, Fail));
    }
}

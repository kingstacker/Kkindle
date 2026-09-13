using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class ReaderAppearanceSettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ReaderAppearance\":null}")]
    public void LegacySettingsKeepTheOriginalBlackAndWhiteDefault(string json)
    {
        var settings = AppSettings.Normalize(JsonSerializer.Deserialize<AppSettings>(json));
        Assert.Equal(ReaderTheme.Classic, settings.ReaderAppearance.Theme);
        Assert.False(settings.ReaderAppearance.PaperEnabled);
        Assert.False(settings.ReaderAppearance.FibersEnabled);
        Assert.Equal(0, settings.ReaderAppearance.EffectivePaperStrength);
    }

    [Theory]
    [InlineData(-8, 0)]
    [InlineData(8, 1)]
    [InlineData(double.NaN, ReaderAppearanceSettings.DefaultPaperStrength)]
    [InlineData(double.PositiveInfinity, ReaderAppearanceSettings.DefaultPaperStrength)]
    public void CorruptValuesNormalizeToSupportedPreferences(double strength, double expected)
    {
        var value = ReaderAppearanceSettings.Normalize(new()
        {
            Theme = (ReaderTheme)999, PaperStrength = strength, FibersEnabled = true
        });
        Assert.Equal(ReaderTheme.Classic, value.Theme);
        Assert.Equal(expected, value.PaperStrength);
        Assert.True(value.FibersEnabled);
    }

    [Fact]
    public void TurningPaperOffRetainsIndependentFiberAndStrengthPreferences()
    {
        var enabled = new ReaderAppearanceSettings
        {
            Theme = ReaderTheme.WarmBrown, PaperEnabled = true, PaperStrength = 0.8, FibersEnabled = true
        };
        var disabled = ReaderAppearanceSettings.Normalize(enabled with { PaperEnabled = false });
        Assert.Equal(0, disabled.EffectivePaperStrength);
        Assert.Equal(enabled, disabled with { PaperEnabled = true });
        Assert.InRange((enabled with { Theme = ReaderTheme.Night }).EffectivePaperStrength, 0.1, 0.4);
        Assert.DoesNotContain(nameof(ReaderAppearanceSettings.EffectivePaperStrength), JsonSerializer.Serialize(enabled));
    }

    [Theory]
    [InlineData(ReaderTheme.Classic)]
    [InlineData(ReaderTheme.Night)]
    [InlineData(ReaderTheme.Green)]
    [InlineData(ReaderTheme.WarmBrown)]
    [InlineData(ReaderTheme.Ivory)]
    public async Task AppearancePersistsAlongsideLayoutWithoutReplacingIt(ReaderTheme theme)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var settings = new AppSettings
            {
                ReaderAppearance = new() { Theme = theme, PaperEnabled = true, PaperStrength = 0.65, FibersEnabled = true },
                DefaultReaderLayout = new(FontScale: 1.4, LineHeight: 2.1, VerticalWriting: true)
            };
            await new AppSettingsStore(paths).SaveAsync(settings);
            var restored = await new AppSettingsStore(paths).LoadAsync();
            Assert.Equal(settings.ReaderAppearance, restored.ReaderAppearance);
            Assert.Equal(settings.DefaultReaderLayout, restored.DefaultReaderLayout);
        }
        finally { TestHelpers.TryDelete(root); }
    }
}

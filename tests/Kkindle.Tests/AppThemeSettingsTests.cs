using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class AppThemeSettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"MainTheme\":-1}")]
    [InlineData("{\"MainTheme\":999}")]
    public void OlderOrUnsupportedPreferencesKeepBlackAndWhite(string json)
    {
        var settings = AppSettings.Normalize(JsonSerializer.Deserialize<AppSettings>(json));
        Assert.Equal(AppTheme.Classic, settings.MainTheme);
        Assert.Equal(ReaderTheme.Classic, settings.ReaderAppearance.Theme);
        Assert.False(settings.ReaderAppearance.PaperEnabled);
    }

    [Theory]
    [InlineData(AppTheme.Classic)]
    [InlineData(AppTheme.Night)]
    [InlineData(AppTheme.Green)]
    [InlineData(AppTheme.WarmBrown)]
    [InlineData(AppTheme.Ivory)]
    public async Task StartupAndAsyncLoadsPreserveMainAndReaderThemesIndependently(AppTheme theme)
    {
        var directory = TestHelpers.CreateTempDirectory();
        try
        {
            var store = new AppSettingsStore(new AppPaths(directory));
            var settings = new AppSettings
            {
                MainTheme = theme,
                ReaderAppearance = new() { Theme = ReaderTheme.Ivory, PaperEnabled = true, FibersEnabled = true },
                DefaultReaderLayout = new(FontScale: 1.3, LineHeight: 1.8)
            };
            await store.SaveAsync(settings);
            Assert.Equal(settings, await store.LoadAsync());
            Assert.Equal(settings, store.LoadSynchronously());
        }
        finally { TestHelpers.TryDelete(directory); }
    }
}

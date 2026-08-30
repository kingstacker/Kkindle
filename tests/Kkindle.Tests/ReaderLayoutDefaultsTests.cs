using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class ReaderLayoutDefaultsTests
{
    [Fact]
    public void DefaultsAreHorizontalAndReadable()
    {
        var defaults = new ReaderLayoutSettings();
        Assert.Equal(1.2, defaults.FontScale);
        Assert.Equal(1.8, defaults.LineHeight);
        Assert.Equal(1200, defaults.MaxWidth);
        Assert.Equal(24, defaults.BodyPadding);
        Assert.Equal(ReaderFontDefaults.DefaultFamily, defaults.FontFamily);
        Assert.Equal(1, defaults.FlowMode);
        Assert.False(defaults.VerticalWriting);
        Assert.False(defaults.TwoPageMode);
        Assert.True(defaults.ParagraphIndent);
    }

    [Fact]
    public void NormalizeClampsOutOfRangeValuesBackToSupportedRanges()
    {
        var corrupt = new ReaderLayoutSettings(
            FontScale: 9.0,
            LineHeight: 0.2,
            MaxWidth: 40,
            BodyPadding: 500,
            FlowMode: 7,
            VerticalWriting: true,
            TwoPageMode: true);

        var normalized = ReaderLayoutDefaults.Normalize(corrupt);

        Assert.Equal(ReaderLayoutDefaults.MaxFontScale, normalized.FontScale);
        Assert.Equal(ReaderLayoutDefaults.MinLineHeight, normalized.LineHeight);
        Assert.Equal(ReaderLayoutDefaults.MinMaxWidth, normalized.MaxWidth);
        Assert.Equal(ReaderLayoutDefaults.MaxBodyPadding, normalized.BodyPadding);
        Assert.Equal(1, normalized.FlowMode); // vertical writing always uses pagination
        Assert.True(normalized.VerticalWriting); // user choice is preserved
        Assert.False(normalized.TwoPageMode); // vertical writing only supports a single page
    }

    [Fact]
    public void NormalizeTreatsNonFiniteValuesAsSafeDefaults()
    {
        var bad = new ReaderLayoutSettings(
            FontScale: double.NaN,
            LineHeight: double.PositiveInfinity,
            MaxWidth: double.NaN,
            BodyPadding: double.NaN,
            FlowMode: 0,
            VerticalWriting: false);

        var normalized = ReaderLayoutDefaults.Normalize(bad);

        Assert.Equal(ReaderLayoutDefaults.DefaultFontScale, normalized.FontScale);
        Assert.Equal(ReaderLayoutDefaults.DefaultLineHeight, normalized.LineHeight);
        Assert.Equal(ReaderLayoutDefaults.DefaultMaxWidth, normalized.MaxWidth);
        Assert.Equal(ReaderLayoutDefaults.DefaultBodyPadding, normalized.BodyPadding);
    }

    [Fact]
    public void NormalizeForcesVerticalWritingToSinglePageMode()
    {
        var invalid = new ReaderLayoutSettings(FlowMode: 3, VerticalWriting: true);
        var normalized = ReaderLayoutDefaults.Normalize(invalid);
        Assert.Equal(1, normalized.FlowMode);
        Assert.False(normalized.TwoPageMode);

        var twoPage = new ReaderLayoutSettings(FlowMode: 1, VerticalWriting: true, TwoPageMode: true);
        var normalizedTwoPage = ReaderLayoutDefaults.Normalize(twoPage);
        Assert.Equal(1, normalizedTwoPage.FlowMode);
        Assert.False(normalizedTwoPage.TwoPageMode);
    }

    [Fact]
    public void NormalizeKeepsValidSettingsUntouched()
    {
        var valid = new ReaderLayoutSettings(
            FontScale: 1.2,
            LineHeight: 2.1,
            MaxWidth: 960,
            BodyPadding: 96,
            FontFamily: "SimSun",
            FlowMode: 1,
            VerticalWriting: false,
            TwoPageMode: true);

        var normalized = ReaderLayoutDefaults.Normalize(valid);
        Assert.Equal(valid, normalized);
    }

    [Fact]
    public void NormalizeMigratesLegacyEmptyFontToJinghuaLaosongti()
    {
        var normalized = ReaderLayoutDefaults.Normalize(new ReaderLayoutSettings(FontFamily: string.Empty));

        Assert.Equal(ReaderFontDefaults.DefaultFamily, normalized.FontFamily);
    }

    [Fact]
    public void ApplyGlobalPreferencesKeepsBookTypographyAndUsesGlobalDirection()
    {
        var bookLayout = new ReaderLayoutSettings(
            FontScale: 1.5,
            LineHeight: 2.2,
            MaxWidth: 960,
            BodyPadding: 72,
            FontFamily: "SimSun",
            FlowMode: 0,
            VerticalWriting: false,
            TwoPageMode: true)
        {
            ParagraphIndent = true
        };
        var globalLayout = new ReaderLayoutSettings(
            FontScale: 1.0,
            LineHeight: 1.5,
            FlowMode: 1,
            VerticalWriting: true,
            TwoPageMode: false)
        {
            ParagraphIndent = false
        };

        var merged = ReaderLayoutDefaults.ApplyGlobalPreferences(bookLayout, globalLayout);

        Assert.Equal(bookLayout.FontScale, merged.FontScale);
        Assert.Equal(bookLayout.LineHeight, merged.LineHeight);
        Assert.Equal(bookLayout.MaxWidth, merged.MaxWidth);
        Assert.Equal(bookLayout.BodyPadding, merged.BodyPadding);
        Assert.Equal(bookLayout.FontFamily, merged.FontFamily);
        Assert.True(merged.VerticalWriting);
        Assert.False(merged.ParagraphIndent);
        Assert.Equal(1, merged.FlowMode);
        Assert.False(merged.TwoPageMode);
    }
}

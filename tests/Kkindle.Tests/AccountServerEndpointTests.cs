using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class AccountServerEndpointTests
{
    [Theory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public void MissingOverrideUsesTheSharedCompiledDefault(AccountRegion region)
    {
        var configured = region == AccountRegion.China
            ? AccountServerEndpoints.ChinaDefaultServerUrl
            : AccountServerEndpoints.InternationalDefaultServerUrl;
        Assert.Equal(AccountValidation.ServerUri(configured).AbsoluteUri.TrimEnd('/'),
            AccountServerEndpoints.Resolve(region, null));
    }

    [Theory]
    [InlineData(AccountRegion.China, "KKINDLE_ACCOUNT_SERVER_CHINA")]
    [InlineData(AccountRegion.International, "KKINDLE_ACCOUNT_SERVER_INTERNATIONAL")]
    public void RegionOverridesAreIndependentAndNormalised(AccountRegion region, string variable)
    {
        Assert.Equal(variable, AccountServerEndpoints.EnvironmentVariable(region));
        Assert.Equal("https://deployment.example.invalid/accounts",
            AccountServerEndpoints.Resolve(region, "  https://deployment.example.invalid/accounts///  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-url")]
    [InlineData("http://deployment.example.invalid")]
    [InlineData("https://fictional-user:fictional-password@deployment.example.invalid")]
    [InlineData("https://deployment.example.invalid/?token=fictional-value")]
    [InlineData("https://deployment.example.invalid/#fragment")]
    [InlineData("ftp://deployment.example.invalid")]
    public void InvalidOverrideFailsWithoutEchoingTheValueOrFallingBack(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => AccountServerEndpoints.Resolve(AccountRegion.China, value));
        Assert.Contains(AccountServerEndpoints.ChinaEnvironmentVariable, error.Message);
        Assert.DoesNotContain("fictional-password", error.ToString());
        Assert.DoesNotContain("fictional-value", error.ToString());
        Assert.DoesNotContain("deployment.example.invalid", error.ToString());
    }

    [Theory]
    [InlineData("http://localhost:59000")]
    [InlineData("http://127.0.0.1:59000")]
    [InlineData("http://[::1]:59000")]
    [InlineData("http://10.0.2.2:59000")]
    public void PlainHttpRemainsRestrictedToDevelopmentHosts(string value) =>
        Assert.Equal(value, AccountServerEndpoints.Resolve(AccountRegion.International, value));

    [Theory]
    [InlineData(AccountRegion.China, "http://127.0.0.1:5088")]
    [InlineData(AccountRegion.International, "http://127.0.0.1:5089")]
    public void ProtocolFixtureListeningAddressesDoNotDependOnDeploymentOverrides(AccountRegion region, string expected)
    {
        Assert.Equal(expected, AccountServerEndpoints.LocalDevelopmentServerUrl(region));
        Assert.Equal("https://deployment.example.invalid", AccountServerEndpoints.Resolve(region, "https://deployment.example.invalid"));
        Assert.Equal(expected, AccountServerEndpoints.LocalDevelopmentServerUrl(region));
    }

    [Theory]
    [InlineData(AccountRegion.China, "http://localhost:5088", true)]
    [InlineData(AccountRegion.China, "http://10.0.2.2:5088", true)]
    [InlineData(AccountRegion.International, "http://[::1]:5089", true)]
    [InlineData(AccountRegion.China, "http://localhost:5089", false)]
    [InlineData(AccountRegion.China, "http://localhost:5088/custom", false)]
    [InlineData(AccountRegion.China, "https://localhost:5088", false)]
    [InlineData(AccountRegion.China, "https://custom.example.invalid", false)]
    public void LegacyInferenceOnlyRecognisesTheMatchingLocalRegionDefault(AccountRegion region, string url, bool expected) =>
        Assert.Equal(expected, AccountServerEndpoints.IsLegacyDevelopmentDefault(region, url));

    [Fact]
    public void UnknownRegionCannotSelectEitherServer()
    {
        var unknown = (AccountRegion)99;
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountServerEndpoints.Resolve(unknown, "https://deployment.example.invalid"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountServerEndpoints.LocalDevelopmentServerUrl(unknown));
    }
}

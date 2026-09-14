using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class KindleWebNavigationPolicyTests
{
    [Theory]
    [InlineData("https://www.amazon.com/sendtokindle")]
    [InlineData("https://www.amazon.com/ap/signin?openid.return_to=https%3A%2F%2Fwww.amazon.com%2Fsendtokindle")]
    [InlineData("https://amazon.com/ap/mfa")]
    [InlineData("https://www.amazon.co.jp/sendtokindle")]
    [InlineData("https://www.amazon.co.uk/sendtokindle")]
    [InlineData("https://WWW.AMAZON.DE:443/ap/signin")]
    public void AllowsOfficialHttpsPagesAndAccountRedirects(string url) =>
        Assert.True(KindleWebNavigationPolicy.IsAllowed(new Uri(url)));

    [Theory]
    [InlineData("http://www.amazon.com/ap/signin")]
    [InlineData("https://www.amazon.com.evil.example/ap/signin")]
    [InlineData("https://fakeamazon.com/sendtokindle")]
    [InlineData("https://www.amazon.com@evil.example/ap/signin")]
    [InlineData("https://evil.example@www.amazon.com/ap/signin")]
    [InlineData("https://www.amazon.com:8443/ap/signin")]
    [InlineData("https://amazon.com.cn/sendtokindle")]
    [InlineData("https://example.com/?next=https://www.amazon.com")]
    [InlineData("file:///C:/Users/example/private.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("about:blank")]
    public void RejectsOtherOriginsInsecureNavigationAndLocalSchemes(string url) =>
        Assert.False(KindleWebNavigationPolicy.IsAllowed(new Uri(url)));

    [Fact]
    public void RejectsMissingAndRelativeAddresses()
    {
        Assert.False(KindleWebNavigationPolicy.IsAllowed(null));
        Assert.False(KindleWebNavigationPolicy.IsAllowed(new Uri("/ap/signin", UriKind.Relative)));
        Assert.False(KindleWebNavigationPolicy.IsAuthenticationPage(null));
        Assert.False(KindleWebNavigationPolicy.IsAuthenticationPage(new Uri("/ap/signin", UriKind.Relative)));
    }

    [Theory]
    [InlineData("https://www.amazon.com/ap/signin")]
    [InlineData("https://www.amazon.com/-/zh/ap/signin?test=1")]
    [InlineData("https://www.amazon.com/-/en-US/ap/mfa")]
    [InlineData("https://www.amazon.com/ap/cvf/verify")]
    [InlineData("https://www.amazon.com/ax/claim")]
    [InlineData("https://www.amazon.com:443/ap/signin")]
    public void AuthenticationUsesOnlyKnownOfficialForms(string url)
    {
        Assert.True(KindleWebNavigationPolicy.IsAuthenticationPage(new Uri(url)));
        Assert.False(KindleWebNavigationPolicy.IsUploadPage(new Uri(url)));
    }

    [Theory]
    [InlineData("https://www.amazon.com/sendtokindle")]
    [InlineData("https://www.amazon.com/ap/register")]
    [InlineData("https://www.amazon.com/ap/forgotpassword")]
    [InlineData("https://www.amazon.com/ap/signin/unrecognized")]
    [InlineData("https://www.amazon.com/ax/claim/unrecognized")]
    [InlineData("https://www.amazon.co.jp/ap/signin")]
    [InlineData("https://other.amazon.com/ap/signin")]
    [InlineData("https://amazon.com/ap/signin")]
    [InlineData("http://www.amazon.com/ap/signin")]
    [InlineData("https://www.amazon.com.evil.example/ap/signin")]
    [InlineData("https://www.amazon.com:8443/ap/signin")]
    [InlineData("https://evil@www.amazon.com/ap/signin")]
    public void AuthenticationDoesNotInheritTheBroaderNavigationAllowlist(string url) =>
        Assert.False(KindleWebNavigationPolicy.IsAuthenticationPage(new Uri(url)));
}

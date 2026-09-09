using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class RadioplayerAuthTests
{
    [Fact]
    public void ApplyToken_UrlWithoutQuery_AddsStAndGts()
    {
        var url = RadioplayerAuth.ApplyToken("https://cdn.radio-holding.ru/marusya_default", "abc-123", "1788935940");

        Assert.Contains("st=abc-123", url);
        Assert.Contains("gts=1788935940", url);
    }

    [Fact]
    public void ApplyToken_UrlWithStaleTokenInQuery_ReplacesIt()
    {
        var url = RadioplayerAuth.ApplyToken(
            "https://cdn.radio-holding.ru/marusya_default?st=old&gts=111", "fresh-token", "222");

        Assert.Contains("st=fresh-token", url);
        Assert.Contains("gts=222", url);
        Assert.DoesNotContain("st=old", url);
        Assert.DoesNotContain("gts=111", url);
    }

    [Fact]
    public void ApplyToken_UrlWithUnrelatedQueryParam_KeepsIt()
    {
        var url = RadioplayerAuth.ApplyToken(
            "https://cdn.radio-holding.ru/marusya_default?foo=bar", "tok", "333");

        Assert.Contains("foo=bar", url);
        Assert.Contains("st=tok", url);
        Assert.Contains("gts=333", url);
    }
}

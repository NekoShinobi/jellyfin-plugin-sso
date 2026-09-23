using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebResponse = Jellyfin.Plugin.SSO_Auth.WebResponse;
using Xunit;

namespace SSO_Auth.Tests;

public class WebResponseTests
{
    [Theory]
    [InlineData("", "OID", false)]
    [InlineData("/jellyfin", "OID", true)]
    [InlineData("/jellyfin", "SAML", false)]
    [InlineData("/jellyfin/nested", "SAML", true)]
    public void CompletionPreservesAssetPathsAndTransactionData(string basePath, string mode, bool linking)
    {
        Guid? target = linking ? Guid.NewGuid() : null;
        var html = WebResponse.Completion(basePath, mode, "provider", "proof", target);
        Assert.Contains($"href=\"{basePath}/web/\"", html);
        Assert.Contains($"href=\"{basePath}/web/themes/dark/theme.css\"", html);
        Assert.Contains($"href=\"{basePath}/SSOViews/style.css\"", html);
        Assert.Contains($"src=\"{basePath}/SSOViews/complete.js\"", html);
        using var data = ReadData(html);
        Assert.Equal(basePath, data.RootElement.GetProperty("basePath").GetString());
        Assert.Equal(mode, data.RootElement.GetProperty("mode").GetString());
        Assert.Equal("provider", data.RootElement.GetProperty("provider").GetString());
        Assert.Equal("proof", data.RootElement.GetProperty("code").GetString());
        Assert.Equal(target?.ToString(), data.RootElement.GetProperty("target").GetString());
    }

    [Fact]
    public void CompletionEncodesValuesAndDoesNotExpandTokensInsideThem()
    {
        const string basePath = "/base\"<&/{{data}}";
        const string provider = "</script><script>alert(1)</script>{{basePath}}";
        const string code = "{{data}}&\"<";
        var html = WebResponse.Completion(basePath, "OID", provider, code, null);
        Assert.Contains($"href=\"{WebUtility.HtmlEncode(basePath)}/web/\"", html);
        Assert.Contains($"src=\"{WebUtility.HtmlEncode(basePath)}/SSOViews/complete.js\"", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Equal(2, Regex.Matches(html, "<script\\b").Count);
        using var data = ReadData(html);
        Assert.Equal(basePath, data.RootElement.GetProperty("basePath").GetString());
        Assert.Equal(provider, data.RootElement.GetProperty("provider").GetString());
        Assert.Equal(code, data.RootElement.GetProperty("code").GetString());
    }

    private static JsonDocument ReadData(string html)
    {
        var match = Regex.Match(html, "<script id=\"sso-data\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success);
        return JsonDocument.Parse(match.Groups[1].Value);
    }
}

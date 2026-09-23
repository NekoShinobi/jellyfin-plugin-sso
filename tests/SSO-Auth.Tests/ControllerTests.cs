using Jellyfin.Plugin.SSO_Auth.Api;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SSO_Auth.Tests;

public class ControllerTests
{
    [Theory]
    [InlineData("provider")]
    [InlineData("protocol")]
    [InlineData("browser")]
    [InlineData("purpose")]
    public async Task CompletionCannotCrossTransactionBoundaries(string mismatch)
    {
        using var transactions = new LoginTransactions(TimeProvider.System);
        var config = new PluginConfiguration(); config.OidConfigs["test"] = new OidConfig { Enabled = true };
        var providers = new ProviderStore(() => config, c => config = c);
        var tx = new LoginTransaction("OID", "test", "https://jf/sso/OID/redirect/test", LoginTransactions.Hash("browser"), mismatch == "purpose" ? Guid.NewGuid() : null, ConfigurationMigration.Fingerprint(config.OidConfigs["test"]), config.OidConfigs["test"]);
        transactions.Add("complete:proof", new LoginCompletion(tx, new("issuer", "sub", "name", [])), TimeSpan.FromMinutes(2));
        var context = new DefaultHttpContext(); context.Request.Headers.Cookie = "sso-" + LoginTransactions.Hash("proof") + "=" + (mismatch == "browser" ? "other" : "browser");
        var controller = new SSOController(providers, transactions, null!, null!, null!, null!, NullLogger<SSOController>.Instance) { ControllerContext = new ControllerContext { HttpContext = context } };
        var response = Assert.IsType<ObjectResult>(await controller.Authenticate(mismatch == "protocol" ? "SAML" : "OID", mismatch == "provider" ? "other" : "test", new AuthResponse { Data = "proof" }));
        Assert.Equal(403, response.StatusCode);
        Assert.Equal(1, transactions.Count);
    }

    [Fact]
    public async Task CallbackMustMatchTheOriginalAliasAndPublicUrl()
    {
        using var transactions = new LoginTransactions(TimeProvider.System);
        var config = ConfigurationMigration.Normalize(new PluginConfiguration()); config.OidConfigs["test"] = new OidConfig { Enabled = true };
        ConfigurationMigration.NormalizeProvider(config.OidConfigs["test"]);
        var providers = new ProviderStore(() => config, c => config = c);
        var tx = new LoginTransaction("OID", "test", "https://jf:9443/base/sso/OID/redirect/test", LoginTransactions.Hash("browser"), null, ConfigurationMigration.Fingerprint(config.OidConfigs["test"]), config.OidConfigs["test"]);
        transactions.Add("flow:state", tx, TimeSpan.FromMinutes(2));
        var context = new DefaultHttpContext(); context.Request.Scheme = "https"; context.Request.Host = new HostString("jf", 9443); context.Request.PathBase = "/base"; context.Request.Path = "/sso/OID/r/test";
        context.Request.Headers.Cookie = "sso-" + LoginTransactions.Hash("state") + "=browser";
        var controller = new SSOController(providers, transactions, null!, null!, null!, null!, NullLogger<SSOController>.Instance) { ControllerContext = new ControllerContext { HttpContext = context } };
        var response = Assert.IsType<ObjectResult>(await controller.OidPost("test", "state"));
        Assert.Equal(400, response.StatusCode);
        Assert.Contains("callback URL", System.Text.Json.JsonSerializer.Serialize(response.Value));
    }

    [Theory]
    [InlineData("names")]
    [InlineData("get")]
    [InlineData("delete")]
    public async Task UnknownProtocolsReturnStructured404WithoutChangingConfiguration(string action)
    {
        var config = new PluginConfiguration();
        config.OidConfigs["test"] = new() { Enabled = true };
        var before = System.Text.Json.JsonSerializer.Serialize(config);
        var writes = 0;
        var providers = new ProviderStore(() => config, c => { config = c; writes++; });
        var controller = ConfigurationController(providers);

        var result = await (action switch
        {
            "names" => controller.Names("invalid"),
            "get" => controller.Get("invalid"),
            _ => controller.Delete("invalid", "test"),
        });

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, response.StatusCode);
        Assert.Contains("Unknown authentication protocol.", System.Text.Json.JsonSerializer.Serialize(response.Value));
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal(0, writes);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(config));
    }

    [Theory]
    [InlineData("OID")]
    [InlineData("SAML")]
    public async Task InvalidProviderUpdatesReturnStructured400AndPreserveSavedConfiguration(string mode)
    {
        var config = new PluginConfiguration();
        config.OidConfigs["test"] = new() { Enabled = true, OidSecret = "saved-secret" };
        config.SamlConfigs["test"] = new() { Enabled = true, SamlIssuer = "saved-issuer" };
        var before = System.Text.Json.JsonSerializer.Serialize(config);
        var writes = 0;
        var providers = new ProviderStore(() => config, c => { config = c; writes++; });
        var controller = ConfigurationController(providers);
        var permissions = new SerializableDictionary<string, bool> { ["unsupported"] = true };

        var result = mode == "OID"
            ? await controller.AddOid("test", new OidConfig { PermissionDefaults = permissions })
            : await controller.AddSaml("test", new SamlConfig { PermissionDefaults = permissions });

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, response.StatusCode);
        Assert.Contains("unsupported permission", System.Text.Json.JsonSerializer.Serialize(response.Value));
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal(0, writes);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(config));
    }

    [Theory]
    [InlineData("OID")]
    [InlineData("SAML")]
    public async Task ValidConfigurationActionsKeepTheirSuccessfulResponses(string mode)
    {
        var config = new PluginConfiguration();
        var providers = new ProviderStore(() => config, c => config = c);
        var controller = ConfigurationController(providers);
        var result = mode == "OID"
            ? await controller.AddOid("test", new OidConfig { Enabled = true })
            : await controller.AddSaml("test", new SamlConfig { Enabled = true });
        Assert.IsType<NoContentResult>(result);
        Assert.True(providers.Get(mode, "test").Enabled);
        Assert.IsType<OkObjectResult>(await controller.Get(mode));
        var names = Assert.IsType<OkObjectResult>(await controller.Names(mode));
        Assert.Equal(new[] { "test" }, Assert.IsAssignableFrom<IEnumerable<string>>(names.Value));
        Assert.IsType<NoContentResult>(await controller.Delete(mode, "test"));
        Assert.Empty(config.OidConfigs);
        Assert.Empty(config.SamlConfigs);
    }

    private static SSOController ConfigurationController(ProviderStore providers) =>
        new(providers, null!, null!, null!, null!, null!, NullLogger<SSOController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

}

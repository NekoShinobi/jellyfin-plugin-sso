using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Services;
using Xunit;

namespace SSO_Auth.Tests;

public class MigrationTests
{
    [Theory]
    [InlineData("legacy-3.xml", "Legacy-OIDC", "synthetic-3-secret", "legacy-oidc-user")]
    [InlineData("legacy-4.xml", "Case-Sensitive", "synthetic-secret", "legacy-name")]
    public void HistoricalSchemasPreserveCredentialsUserIdsAndFolders(string file, string name, string secret, string oldName)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        var config = ConfigurationMigration.Normalize((PluginConfiguration)serializer.Deserialize(input)!);
        var provider = config.OidConfigs[name];
        Assert.Equal(secret, provider.OidSecret);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), provider.CanonicalLinks[oldName]);
        // Both fixtures leave role-based libraries off, so their inactive mappings are dropped rather than enabled.
        Assert.Empty(provider.FolderRoleMapping);
        Assert.Empty(provider.GroupPermissions);
        Assert.Equal(new Dictionary<string, bool> { ["IsAdministrator"] = false, ["EnableLiveTvAccess"] = false, ["EnableLiveTvManagement"] = false }, provider.PermissionDefaults);
        if (file == "legacy-3.xml")
        {
            Assert.Equal("22222222222222222222222222222222", Assert.Single(provider.EnabledFolders));
        }

        Assert.Empty(provider.SubjectLinks);
        var once = JsonSerializer.Serialize(config);
        using var saved = new StringWriter(); serializer.Serialize(saved, config);
        var twice = ConfigurationMigration.Normalize((PluginConfiguration)serializer.Deserialize(new StringReader(saved.ToString()))!);
        Assert.Equal(once, JsonSerializer.Serialize(twice));
        if (file == "legacy-3.xml")
        {
            Assert.Equal("synthetic-ldap-provider", twice.SamlConfigs["Legacy-SAML"].DefaultProvider);
            Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), twice.SamlConfigs["Legacy-SAML"].CanonicalLinks["legacy-saml-user"]);
        }
    }

    [Fact]
    public void FailedConfigurationEditDoesNotOverwriteOtherProviders()
    {
        var config = new PluginConfiguration(); config.OidConfigs["preserved"] = new OidConfig { OidSecret = "synthetic" };
        var store = new ProviderStore(() => config, c => config = c);
        Assert.Throws<SsoException>(() => store.Edit(c => { c.OidConfigs.Clear(); c.SchemaVersion = int.MaxValue; }));
        Assert.Equal("synthetic", config.OidConfigs["preserved"].OidSecret);
    }
}

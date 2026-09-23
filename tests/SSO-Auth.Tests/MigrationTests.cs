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

    [Theory]
    [InlineData("OID")]
    [InlineData("SAML")]
    public void ValidatedSnapshotIncludesLatestLinksButIsDetachedAndStillRejectsChangedSettings(string mode)
    {
        var config = new PluginConfiguration();
        config.OidConfigs["test"] = new() { Enabled = true };
        config.SamlConfigs["test"] = new() { Enabled = true };
        var store = new ProviderStore(() => config, c => config = c);
        var original = store.Get(mode, "test");
        var transaction = new LoginTransaction(mode, "test", "https://jf/callback", "browser", null, ConfigurationMigration.Fingerprint(original), original);
        var id = Guid.NewGuid();
        store.Edit(c => ProviderStore.Find(c, mode, "test").SubjectLinks["new-subject"] = id);
        var current = store.GetUnchanged(transaction);
        Assert.Equal(id, current.SubjectLinks["new-subject"]);
        current.SubjectLinks.Clear();
        Assert.Equal(id, store.Get(mode, "test").SubjectLinks["new-subject"]);
        store.Edit(c => ProviderStore.Find(c, mode, "test").Roles = ["changed"]);
        Assert.Throws<SsoException>(() => store.GetUnchanged(transaction));
        store.Edit(c => { var provider = ProviderStore.Find(c, mode, "test"); provider.Roles = []; provider.Enabled = false; });
        Assert.Equal(404, Assert.Throws<SsoException>(() => store.GetUnchanged(transaction)).Status);
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

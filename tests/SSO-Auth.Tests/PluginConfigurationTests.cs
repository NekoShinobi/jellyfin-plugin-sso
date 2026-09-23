using System.Xml.Serialization;
using Jellyfin.Plugin.SSO_Auth;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace SSO_Auth.Tests;

public class PluginConfigurationTests
{
    [Theory]
    [InlineData("OID")]
    [InlineData("SAML")]
    public void StaleDashboardSavePreservesLatestAccountStateWithoutRestoringDeletedProviders(string mode)
    {
        var directory = Directory.CreateTempSubdirectory("sso-config-test-");
        try
        {
            var paths = new Mock<IApplicationPaths>();
            paths.SetupGet(p => p.PluginConfigurationsPath).Returns(directory.FullName);
            paths.SetupGet(p => p.DataPath).Returns(directory.FullName);
            paths.SetupGet(p => p.PluginsPath).Returns(directory.FullName);
            paths.SetupGet(p => p.ProgramDataPath).Returns(directory.FullName);
            var serializer = new Mock<IXmlSerializer>();
            serializer.Setup(s => s.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
                .Callback((object value, string path) =>
                {
                    using var output = File.Create(path);
                    new XmlSerializer(value.GetType()).Serialize(output, value);
                });
            var plugin = new SSOPlugin(paths.Object, serializer.Object);
            var store = new ProviderStore(() => plugin.Configuration, plugin.PersistConfiguration, plugin.ConfigurationGate);
            store.Edit(config =>
            {
                config.OidConfigs["same-name"] = ProviderFixtures.Oid();
                config.SamlConfigs["same-name"] = ProviderFixtures.Saml();
                config.OidConfigs["deleted"] = new();
                config.SamlConfigs["deleted"] = new();
                foreach (var protocol in new[] { "OID", "SAML" })
                {
                    var provider = ProviderStore.Find(config, protocol, "same-name");
                    provider.SubjectLinks["removed-subject"] = Guid.NewGuid();
                    provider.CanonicalLinks["removed-name"] = Guid.NewGuid();
                }
            });
            var stale = store.Snapshot();
            stale.OidConfigs.Remove("deleted");
            stale.SamlConfigs.Remove("deleted");
            stale.OidConfigs["added"] = new() { OidSecret = "keep-new-provider" };
            stale.SamlConfigs["added"] = new() { SamlIssuer = "keep-new-provider" };
            ProviderStore.Find(stale, mode, "same-name").DefaultProvider = "ldap";
            var oidUser = Guid.NewGuid();
            var samlUser = Guid.NewGuid();
            store.Edit(config =>
            {
                foreach (var (protocol, id) in new[] { ("OID", oidUser), ("SAML", samlUser) })
                {
                    var provider = ProviderStore.Find(config, protocol, "same-name");
                    provider.SubjectLinks.Clear();
                    provider.CanonicalLinks.Clear();
                    provider.SubjectLinks["new-subject"] = id;
                    provider.CanonicalLinks["new-name"] = id;
                    provider.UserRoleSnapshots[id.ToString("N")] = new() { Roles = [protocol], ObservedAt = DateTimeOffset.UtcNow };
                }
            });

            plugin.UpdateConfiguration(stale);

            using var input = File.OpenRead(plugin.ConfigurationFilePath);
            var saved = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(input)!;
            Assert.Equal("ldap", ProviderStore.Find(saved, mode, "same-name").DefaultProvider);
            foreach (var (protocol, id) in new[] { ("OID", oidUser), ("SAML", samlUser) })
            {
                var provider = ProviderStore.Find(saved, protocol, "same-name");
                Assert.Equal(id, Assert.Single(provider.SubjectLinks).Value);
                Assert.True(provider.SubjectLinks.ContainsKey("new-subject"));
                Assert.Equal(id, Assert.Single(provider.CanonicalLinks).Value);
                Assert.True(provider.CanonicalLinks.ContainsKey("new-name"));
                Assert.Equal(new[] { protocol }, provider.UserRoleSnapshots[id.ToString("N")].Roles);
            }
            Assert.False(saved.OidConfigs.ContainsKey("deleted"));
            Assert.False(saved.SamlConfigs.ContainsKey("deleted"));
            Assert.Equal("keep-new-provider", saved.OidConfigs["added"].OidSecret);
            Assert.Equal("keep-new-provider", saved.SamlConfigs["added"].SamlIssuer);
            Assert.False(File.Exists(plugin.ConfigurationFilePath + ".tmp"));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}

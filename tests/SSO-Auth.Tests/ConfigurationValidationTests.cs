using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.SSO_Auth;
using Jellyfin.Plugin.SSO_Auth.Api;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace SSO_Auth.Tests;

internal static class ProviderFixtures
{
    private static readonly Lazy<string> Certificate = new(() =>
    {
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=synthetic-idp", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Cert));
    });

    public static OidConfig Oid() => new() { Enabled = true, OidEndpoint = "https://idp.example", OidClientId = "jellyfin", OidSecret = " secret with spaces " };
    public static SamlConfig Saml() => new() { Enabled = true, SamlEndpoint = "https://idp.example/saml", SamlIssuer = "https://idp.example", SamlClientId = "jellyfin", SamlCertificate = Certificate.Value };
}

public class ConfigurationValidationTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("sso-validation-");
        private readonly Mock<IApplicationPaths> _paths = new();
        private readonly Mock<IXmlSerializer> _serializer = new();
        public readonly SSOPlugin Plugin;
        public readonly ProviderStore Store;
        public readonly SSOController Controller;

        public Fixture()
        {
            _paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory.FullName);
            _paths.SetupGet(p => p.DataPath).Returns(_directory.FullName);
            _paths.SetupGet(p => p.PluginsPath).Returns(_directory.FullName);
            _paths.SetupGet(p => p.ProgramDataPath).Returns(_directory.FullName);
            _serializer.Setup(s => s.SerializeToFile(It.IsAny<object>(), It.IsAny<string>())).Callback((object value, string path) =>
            {
                using var output = File.Create(path);
                new XmlSerializer(value.GetType()).Serialize(output, value);
            });
            _serializer.Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>())).Returns((Type type, string path) =>
            {
                using var input = File.OpenRead(path);
                return new XmlSerializer(type).Deserialize(input)!;
            });
            Plugin = Restart();
            Store = new(() => Plugin.Configuration, Plugin.PersistConfiguration, Plugin.ConfigurationGate);
            Controller = new(Store, null!, null!, null!, null!, null!, NullLogger<SSOController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
        }

        public SSOPlugin Restart() => new(_paths.Object, _serializer.Object);
        public void Seed() => Store.Edit(c => { c.OidConfigs["test"] = ProviderFixtures.Oid(); c.SamlConfigs["test"] = ProviderFixtures.Saml(); });
        public Task<ActionResult> Save(string mode, ProviderConfig config) => mode == "OID" ? Controller.AddOid("test", (OidConfig)config) : Controller.AddSaml("test", (SamlConfig)config);
        public void Dispose() => _directory.Delete(true);
    }

    public static IEnumerable<object?[]> Ports => from mode in new[] { "OID", "SAML" }
        from port in new int?[] { null, 1, 9443, 65535, -2, -1, 0, 65536, int.MaxValue }
        select new object?[] { mode, port };

    [Theory]
    [MemberData(nameof(Ports))]
    public async Task ApiPortValidationIsAtomicAndPreservesValidPorts(string mode, int? port)
    {
        using var f = new Fixture(); f.Seed();
        var config = f.Store.Get(mode, "test"); config.PortOverride = port;
        var before = File.ReadAllText(f.Plugin.ConfigurationFilePath);
        var memory = JsonSerializer.Serialize(f.Plugin.Configuration);
        var dashboard = f.Store.Snapshot();
        ProviderStore.Find(dashboard, mode, "test").PortOverride = port;
        if (port is < 1 or > 65535)
        {
            Assert.Contains("PortOverride", Assert.Throws<SsoException>(() => f.Plugin.UpdateConfiguration(dashboard)).Message);
        }
        else
        {
            f.Plugin.UpdateConfiguration(dashboard);
        }
        var response = await f.Save(mode, config);
        if (port is < 1 or > 65535)
        {
            var error = Assert.IsType<ObjectResult>(response);
            Assert.Equal(400, error.StatusCode);
            Assert.Contains("PortOverride", JsonSerializer.Serialize(error.Value));
            Assert.Equal(before, File.ReadAllText(f.Plugin.ConfigurationFilePath));
            Assert.Equal(memory, JsonSerializer.Serialize(f.Plugin.Configuration));
        }
        else
        {
            Assert.IsType<NoContentResult>(response);
            Assert.Equal(port, f.Store.Get(mode, "test").PortOverride);
            Assert.Equal(port, ProviderStore.Find(f.Restart().Configuration, mode, "test").PortOverride);
        }
    }

    [Theory]
    [InlineData("OID", "OidEndpoint", "http://idp.example")]
    [InlineData("OID", "OidClientId", " ")]
    [InlineData("SAML", "SamlEndpoint", "http://idp.example")]
    [InlineData("SAML", "SamlIssuer", "")]
    [InlineData("SAML", "SamlClientId", " ")]
    [InlineData("SAML", "SamlNameIdFormat", "urn:oasis:names:tc:SAML:2.0:nameid-format:transient")]
    [InlineData("SAML", "SamlCertificate", "")]
    [InlineData("SAML", "SamlCertificate", "bad-certificate")]
    [InlineData("OID", "SchemeOverride", "https://")]
    [InlineData("SAML", "SchemeOverride", "https://")]
    public async Task EnabledProviderErrorsRejectBothSavePathsWithoutReplacingWorkingSettings(string mode, string field, string value)
    {
        using var f = new Fixture(); f.Seed();
        var draft = f.Store.Snapshot();
        var provider = ProviderStore.Find(draft, mode, "test");
        provider.GetType().GetProperty(field)!.SetValue(provider, value);
        var before = File.ReadAllText(f.Plugin.ConfigurationFilePath);
        var memory = JsonSerializer.Serialize(f.Plugin.Configuration);
        var expected = field == "SamlCertificate" ? "certificate" : field;
        Assert.Contains(expected, Assert.Throws<SsoException>(() => f.Plugin.UpdateConfiguration(draft)).Message);
        var response = Assert.IsType<ObjectResult>(await f.Save(mode, provider));
        Assert.Equal(400, response.StatusCode);
        Assert.Contains(expected, JsonSerializer.Serialize(response.Value));
        Assert.Equal(before, File.ReadAllText(f.Plugin.ConfigurationFilePath));
        Assert.Equal(memory, JsonSerializer.Serialize(f.Plugin.Configuration));
    }

    [Theory]
    [InlineData("OID")]
    [InlineData("SAML")]
    public async Task DisabledDraftsCanBeSavedButCannotBeEnabledWithoutRequiredFields(string mode)
    {
        using var f = new Fixture();
        ProviderConfig draft = mode == "OID" ? new OidConfig() : new SamlConfig();
        Assert.IsType<NoContentResult>(await f.Save(mode, draft));
        var settings = f.Store.Snapshot();
        f.Plugin.UpdateConfiguration(settings);
        ProviderStore.Find(settings, mode, "test").Enabled = true;
        Assert.Throws<SsoException>(() => f.Plugin.UpdateConfiguration(settings));
        draft.Enabled = true;
        Assert.Equal(400, Assert.IsType<ObjectResult>(await f.Save(mode, draft)).StatusCode);
        Assert.False(f.Store.Get(mode, "test", false).Enabled);
    }

    [Theory]
    [InlineData("OID", -1)]
    [InlineData("OID", 0)]
    [InlineData("OID", 65536)]
    [InlineData("SAML", -1)]
    [InlineData("SAML", 0)]
    [InlineData("SAML", 65536)]
    public void XmlLegacySettingsLoadAndUnchangedProvidersDoNotBlockOtherSaves(string mode, int port)
    {
        using var f = new Fixture();
        f.Store.Edit(c =>
        {
            c.OidConfigs["test"] = new() { Enabled = true };
            c.SamlConfigs["test"] = new() { Enabled = true };
            ProviderStore.Find(c, mode, "test").PortOverride = port;
        });
        var restarted = f.Restart();
        Assert.Equal(port, ProviderStore.Find(restarted.Configuration, mode, "test").PortOverride);
        var draft = ConfigurationMigration.Clone(restarted.Configuration);
        draft.OidConfigs["unrelated"] = new();
        restarted.UpdateConfiguration(draft);
        var accountStore = new ProviderStore(() => restarted.Configuration, restarted.PersistConfiguration);
        accountStore.Edit(c => ProviderStore.Find(c, mode, "test").SubjectLinks["subject"] = Guid.NewGuid());
        var edited = accountStore.Snapshot();
        ProviderStore.Find(edited, mode, "test").Roles = ["changed"];
        Assert.Throws<SsoException>(() => restarted.UpdateConfiguration(edited));
        Assert.Single(ProviderStore.Find(restarted.Configuration, mode, "test").SubjectLinks);
    }

    [Theory]
    [InlineData("OID", -1)]
    [InlineData("OID", 0)]
    [InlineData("SAML", -1)]
    [InlineData("SAML", 0)]
    public async Task ExistingSentinelsArePreservedUntilPortChanges(string mode, int port)
    {
        using var f = new Fixture(); f.Seed();
        f.Store.Edit(c => ProviderStore.Find(c, mode, "test").PortOverride = port);
        var draft = f.Store.Get(mode, "test"); draft.Roles = ["new-role"];
        Assert.IsType<NoContentResult>(await f.Save(mode, draft));
        var settings = f.Store.Snapshot();
        ProviderStore.Find(settings, mode, "test").DefaultProvider = "  ldap  ";
        f.Plugin.UpdateConfiguration(settings);
        Assert.Equal(port, f.Store.Get(mode, "test").PortOverride);
        draft.PortOverride = port == 0 ? -1 : 0;
        Assert.Equal(400, Assert.IsType<ObjectResult>(await f.Save(mode, draft)).StatusCode);
        draft.PortOverride = null;
        Assert.IsType<NoContentResult>(await f.Save(mode, draft));
    }

    [Theory]
    [InlineData("OID", "  ldap  ", "ldap")]
    [InlineData("SAML", "  ldap  ", "ldap")]
    [InlineData("OID", "   ", "")]
    [InlineData("SAML", "   ", "")]
    [InlineData("OID", "  invalid  ", "invalid")]
    [InlineData("SAML", "  invalid  ", "invalid")]
    public async Task XmlAndApiNormalizeOnlyFallbackIdWhitespace(string mode, string value, string expected)
    {
        using var f = new Fixture(); f.Seed();
        var configuration = f.Store.Snapshot();
        ProviderStore.Find(configuration, mode, "test").DefaultProvider = value;
        using (var writer = File.Create(f.Plugin.ConfigurationFilePath)) new XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, configuration);
        Assert.Equal(expected, ProviderStore.Find(f.Restart().Configuration, mode, "test").DefaultProvider);
        var draft = f.Store.Get(mode, "test"); draft.DefaultProvider = value;
        Assert.IsType<NoContentResult>(await f.Save(mode, draft));
        Assert.Equal(expected, f.Store.Get(mode, "test").DefaultProvider);
        Assert.Equal(" secret with spaces ", f.Plugin.Configuration.OidConfigs["test"].OidSecret);
    }

    [Fact]
    public async Task ExplicitHttpOidcOptOutAndBlankPublicClientSecretRemainSupported()
    {
        using var f = new Fixture();
        var config = ProviderFixtures.Oid();
        config.OidEndpoint = "http://idp.example";
        config.DisableHttps = true;
        config.OidSecret = "";
        Assert.IsType<NoContentResult>(await f.Save("OID", config));
        Assert.Equal("http://idp.example", ((OidConfig)f.Store.Get("OID", "test")).OidEndpoint);
        config.DisableHttps = false;
        Assert.Equal(400, Assert.IsType<ObjectResult>(await f.Save("OID", config)).StatusCode);
    }

    [Fact]
    public async Task ZitadelScopeIsRequiredOnlyWhenEnabledAndRoundTripsThroughXml()
    {
        using var f = new Fixture();
        var draft = ProviderFixtures.Oid(); draft.UseZitadelRoles = true; draft.RoleClaim = "roles";
        Assert.Equal(400, Assert.IsType<ObjectResult>(await f.Save("OID", draft)).StatusCode);
        draft.Enabled = false;
        Assert.IsType<NoContentResult>(await f.Save("OID", draft));
        draft.Enabled = true; draft.ZitadelOrganizationIds = ["org-a", "org-b"];
        Assert.IsType<NoContentResult>(await f.Save("OID", draft));
        var restored = f.Restart().Configuration.OidConfigs["test"];
        Assert.True(restored.UseZitadelRoles);
        Assert.Equal(draft.ZitadelOrganizationIds, restored.ZitadelOrganizationIds);
        draft.RoleClaim = " ";
        Assert.Equal(400, Assert.IsType<ObjectResult>(await f.Save("OID", draft)).StatusCode);
    }
}

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Services;
using Xunit;

namespace SSO_Auth.Tests;

public class SecurityTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task CompletionIsAtomicBoundAndExpires()
    {
        var clock = new Clock();
        using var store = new LoginTransactions(clock);
        var settings = new OidConfig();
        var transaction = new LoginTransaction("OID", "a", "https://jf/callback", LoginTransactions.Hash("browser"), null, "settings", settings);
        store.Add("proof", transaction, TimeSpan.FromMinutes(2));
        Assert.Throws<SsoException>(() => store.Take<LoginTransaction>("proof", t => t.Provider == "b"));
        Assert.Throws<SsoException>(() => store.Take<LoginTransaction>("proof", t => t.BrowserHash == LoginTransactions.Hash("other browser")));
        var success = 0;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            try
            {
                store.Take<LoginTransaction>("proof", t => t.TargetUser is null);
                Interlocked.Increment(ref success);
            }
            catch (SsoException) { }
        })));
        Assert.Equal(1, success);
        store.Add("expired", transaction, TimeSpan.FromMinutes(2));
        clock.Now += TimeSpan.FromMinutes(3);
        Assert.Throws<SsoException>(() => store.Take<LoginTransaction>("expired", _ => true));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void LoginAndLinkProofsAreNotInterchangeable()
    {
        using var store = new LoginTransactions(TimeProvider.System);
        var target = Guid.NewGuid();
        var tx = new LoginTransaction("OID", "provider", "https://jf/callback", "browser", target, "settings", new OidConfig());
        store.Add("link", tx, TimeSpan.FromMinutes(2));
        Assert.Throws<SsoException>(() => store.Take<LoginTransaction>("link", t => t.TargetUser is null));
        Assert.Throws<SsoException>(() => store.Take<LoginTransaction>("link", t => t.TargetUser == Guid.NewGuid()));
        Assert.Equal(target, store.Take<LoginTransaction>("link", t => t.TargetUser == target).TargetUser);
    }

    [Theory]
    [InlineData("groups", "[\"users\",\"admins\"]")]
    [InlineData("realm_access.roles", "{\"roles\":[\"users\",\"admins\"]}")]
    [InlineData("realm\\.access.roles", "{\"roles\":[\"users\",\"admins\"]}")]
    public void NormalizesArraysAndNestedClaims(string path, string value)
    {
        var claim = path.StartsWith("realm\\.") ? "realm.access" : path.Split('.')[0];
        Assert.Equal(new[] { "users", "admins" }, IdentityPolicy.ReadRoles([new Claim(claim, value)], path));
    }

    [Fact]
    public void RepeatedAndMissingClaimsAreDeterministicAndMalformedClaimsFail()
    {
        Assert.Equal(new[] { "user", "admin" }, IdentityPolicy.ReadRoles([new Claim("groups", "user"), new Claim("groups", "admin"), new Claim("groups", "user")], "groups"));
        Assert.Empty(IdentityPolicy.ReadRoles([], "groups"));
        Assert.Throws<SsoException>(() => IdentityPolicy.ReadRoles([new Claim("groups", "[1]")], "groups"));
        Assert.Throws<SsoException>(() => IdentityPolicy.ReadRoles([new Claim("realm", "{")], "realm.roles"));
    }

    [Fact]
    public void IdentityUsesIssuerAndSubjectInsteadOfDisplayName()
    {
        var identity = new ExternalIdentity("issuer", "subject", "alice", []);
        Assert.Equal(identity.Key, (identity with { DisplayName = "renamed" }).Key);
        Assert.NotEqual(identity.Key, (identity with { Subject = "other" }).Key);
        Assert.NotEqual(identity.Key, (identity with { Issuer = "other" }).Key);
    }

    [Fact]
    public void AdmissionIsIndependentOfAdminAndPolicySynchronization()
    {
        var config = new OidConfig { Roles = ["admitted"], AdminRoles = ["admin"], EnableAuthorization = true };
        ConfigurationMigration.NormalizeProvider(config);
        Assert.Throws<SsoException>(() => IdentityPolicy.Admit(config, ["admin"]));
        Assert.Throws<SsoException>(() => PermissionPolicy.Resolve(config, ["admin"], null));
        Assert.True(PermissionPolicy.Resolve(config, ["admitted", "admin"], null).Values[PermissionKind.IsAdministrator]);
        Assert.False(PermissionPolicy.Resolve(config, ["admitted"], null).Values[PermissionKind.IsAdministrator]);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.1.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void AvatarAddressesMustBePublic(string address, bool expected) => Assert.Equal(expected, AvatarService.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("")]
    [InlineData("<Roles /><OidScopes /><EnabledFolders /><CanonicalLinks />")]
    [InlineData("<Roles xsi:nil=\"true\"/><OidScopes xsi:nil=\"true\"/><EnabledFolders xsi:nil=\"true\"/>")]
    public void LegacyXmlNormalizesWithoutLosingOtherProviderData(string fields)
    {
        var xml = "<PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><OidConfigs>"
            + "<item><key><string>Case-Sensitive</string></key><value><PluginConfiguration><OidSecret>synthetic secret</OidSecret>" + fields
            + "</PluginConfiguration></value></item></OidConfigs><SamlConfigs /></PluginConfiguration>";
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = (PluginConfiguration)serializer.Deserialize(new StringReader(xml))!;
        ConfigurationMigration.Normalize(config);
        Assert.Equal("synthetic secret", config.OidConfigs["Case-Sensitive"].OidSecret);
        Assert.NotNull(config.OidConfigs["Case-Sensitive"].Roles);
        var first = JsonSerializer.Serialize(config);
        ConfigurationMigration.Normalize(config);
        Assert.Equal(first, JsonSerializer.Serialize(config));
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        var roundtrip = (PluginConfiguration)serializer.Deserialize(new StringReader(writer.ToString()))!;
        Assert.Equal("synthetic secret", roundtrip.OidConfigs["Case-Sensitive"].OidSecret);
    }

    [Fact]
    public void MigrationPreservesUserGuidsAndDoesNotCreateStableLinksFromNames()
    {
        var id = Guid.NewGuid();
        var config = new PluginConfiguration();
        config.OidConfigs["provider"] = new OidConfig { CanonicalLinks = new() { ["alice"] = id } };
        ConfigurationMigration.Normalize(config);
        Assert.Equal(id, config.OidConfigs["provider"].CanonicalLinks["alice"]);
        Assert.Empty(config.OidConfigs["provider"].SubjectLinks);
    }
}

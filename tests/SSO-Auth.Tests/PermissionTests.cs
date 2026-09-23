using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Services;
using Xunit;

namespace SSO_Auth.Tests;

public class PermissionTests
{
    private static ProviderConfig Config() => new() { Enabled = true, EnableAuthorization = true, PermissionDefaults = new() };

    [Fact]
    public void CatalogueCoversEveryHostPermission()
    {
        Assert.Equal(Enum.GetNames<PermissionKind>().Order(), PermissionPolicy.Catalogue.Select(p => p.Key).Order());
        Assert.False(PermissionPolicy.Catalogue.Single(p => p.Key == "IsDisabled").Editable);
    }

    [Fact]
    public void GroupsOnlyAddAndUserOverrideWinsLast()
    {
        var config = Config(); var id = Guid.NewGuid();
        config.GroupPermissions = [
            new() { Role = "family", Permissions = new() { ["EnableContentDownloading"] = true } },
            new() { Role = "tv", Permissions = new() { ["EnableLiveTvAccess"] = true } },
        ];
        ConfigurationMigration.NormalizeProvider(config);
        Assert.False(config.PermissionDefaults!["EnableContentDownloading"]);
        var result = PermissionPolicy.Resolve(config, ["family", "tv"], id);
        Assert.True(result.Values[PermissionKind.EnableContentDownloading]);
        Assert.True(result.Values[PermissionKind.EnableLiveTvAccess]);
        Assert.Equal("Group: family", result.Sources[PermissionKind.EnableContentDownloading]);
        Assert.False(result.Values.ContainsKey(PermissionKind.EnableContentDeletion));
        var alone = PermissionPolicy.Resolve(config, ["tv"], id);
        Assert.False(alone.Values[PermissionKind.EnableContentDownloading]);
        Assert.Equal("Everyone", alone.Sources[PermissionKind.EnableContentDownloading]);
        config.UserPermissions.Add(new() { UserId = id, Permissions = new() { ["EnableContentDownloading"] = false } });
        var overridden = PermissionPolicy.Resolve(config, ["family", "tv"], id);
        Assert.False(overridden.Values[PermissionKind.EnableContentDownloading]);
        Assert.Equal("User override", overridden.Sources[PermissionKind.EnableContentDownloading]);
        Assert.True(PermissionPolicy.Resolve(config, ["family"], Guid.NewGuid()).Values[PermissionKind.EnableContentDownloading]);
    }

    [Fact]
    public void AdmissionAndGroupMatchingStayOrdinalAndDuplicateClaimsDoNotDuplicateGrants()
    {
        var config = Config();
        config.Roles = ["family"];
        config.GroupPermissions = [
            new() { Role = "family", Permissions = new() { ["EnableContentDownloading"] = true } },
            new() { Role = "Family", Permissions = new() { ["IsAdministrator"] = true } },
        ];
        ConfigurationMigration.NormalizeProvider(config);
        Assert.Equal(403, Assert.Throws<SsoException>(() => PermissionPolicy.Resolve(config, ["Family"], null)).Status);
        var result = PermissionPolicy.Resolve(config, ["family", "family"], null);
        Assert.True(result.Values[PermissionKind.EnableContentDownloading]);
        Assert.False(result.Values[PermissionKind.IsAdministrator]);
        Assert.Equal("Group: family", result.Sources[PermissionKind.EnableContentDownloading]);
    }

    [Fact]
    public void GroupRulesCannotTurnPermissionsOff()
    {
        var config = Config();
        config.GroupPermissions.Add(new() { Role = "restricted", Permissions = new() { ["EnableContentDownloading"] = false } });
        var error = Assert.Throws<SsoException>(() => ConfigurationMigration.NormalizeProvider(config));
        Assert.Contains("only grant", error.Message);
    }

    [Fact]
    public void RemovedMembershipOrRuleRevokesGrantsAndDefaultsPersistAcrossXml()
    {
        var config = new PluginConfiguration();
        config.OidConfigs["test"] = new() { Enabled = true, EnableAuthorization = true, PermissionDefaults = new(), GroupPermissions = [new() { Role = "downloaders", Permissions = new() { ["EnableContentDownloading"] = true } }] };
        ConfigurationMigration.Normalize(config);
        Assert.True(PermissionPolicy.Resolve(config.OidConfigs["test"], ["downloaders"], null).Values[PermissionKind.EnableContentDownloading]);
        Assert.False(PermissionPolicy.Resolve(config.OidConfigs["test"], [], null).Values[PermissionKind.EnableContentDownloading]);
        Assert.False(PermissionPolicy.Resolve(config.OidConfigs["test"], ["Downloaders"], null).Values[PermissionKind.EnableContentDownloading]);
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var output = new StringWriter(); serializer.Serialize(output, config);
        var saved = (PluginConfiguration)serializer.Deserialize(new StringReader(output.ToString()))!;
        saved.OidConfigs["test"].GroupPermissions.Clear();
        ConfigurationMigration.Normalize(saved);
        Assert.False(PermissionPolicy.Resolve(saved.OidConfigs["test"], ["downloaders"], null).Values[PermissionKind.EnableContentDownloading]);
    }

    [Fact]
    public void GroupLibrariesAddToEveryoneAndAllWins()
    {
        var config = Config(); var id = Guid.NewGuid();
        config.EnabledFolders = ["shared"];
        config.GroupPermissions = [
            new() { Role = "all", LibraryMode = "All" },
            new() { Role = "films", LibraryMode = "Selected", Folders = ["films", "shared"] },
            new() { Role = "music", LibraryMode = "Selected", Folders = ["music"] },
            new() { Role = "none" },
        ];
        ConfigurationMigration.NormalizeProvider(config);
        var result = PermissionPolicy.Resolve(config, ["films", "music", "none"], id);
        Assert.False(result.Values[PermissionKind.EnableAllFolders]);
        Assert.Equal(new[] { "films", "music", "shared" }, result.Folders.Order());
        Assert.Equal(new[] { "shared" }, PermissionPolicy.Resolve(config, ["none"], id).Folders);
        Assert.True(PermissionPolicy.Resolve(config, ["all", "films"], id).Values[PermissionKind.EnableAllFolders]);
        config.UserPermissions.Add(new() { UserId = id, LibraryMode = "Selected", Folders = [] });
        result = PermissionPolicy.Resolve(config, ["all", "films", "music"], id);
        Assert.False(result.Values[PermissionKind.EnableAllFolders]); Assert.Empty(result.Folders);
        Assert.Equal("User override", result.FolderSource);
    }

    [Fact]
    public void ReleaseSettingsBecomeDefaultsAndGroupsAndAreCleared()
    {
        var config = new OidConfig
        {
            Enabled = true, EnableAuthorization = true, AdminRoles = ["admins"], EnableLiveTvRoles = true,
            LiveTvRoles = ["tv", "family"], LiveTvManagementRoles = ["tv"], EnableLiveTvManagement = true,
            EnableFolderRoles = true, EnabledFolders = ["ignored"],
            FolderRoleMapping = [new() { Role = " family ", Folders = ["films"] }, new() { Role = "family", Folders = ["kids"] }],
        };
        ConfigurationMigration.NormalizeProvider(config);
        Assert.Equal(new Dictionary<string, bool> { ["IsAdministrator"] = false, ["EnableLiveTvAccess"] = false, ["EnableLiveTvManagement"] = true }, config.PermissionDefaults);
        Assert.Empty(config.EnabledFolders);
        var family = config.GroupPermissions.Single(g => g.Role == "family");
        Assert.Equal("Selected", family.LibraryMode); Assert.Equal(new[] { "films", "kids" }, family.Folders);
        Assert.Equal(new[] { "EnableLiveTvAccess" }, family.Permissions.Keys);
        Assert.Equal(new[] { "EnableLiveTvAccess", "EnableLiveTvManagement" }, config.GroupPermissions.Single(g => g.Role == "tv").Permissions.Keys.Order());
        Assert.Equal(new[] { "IsAdministrator" }, config.GroupPermissions.Single(g => g.Role == "admins").Permissions.Keys);
        Assert.Empty(config.AdminRoles); Assert.Empty(config.LiveTvRoles); Assert.Empty(config.LiveTvManagementRoles); Assert.Empty(config.FolderRoleMapping);
        Assert.False(config.EnableFolderRoles || config.EnableLiveTvRoles || config.EnableLiveTv || config.EnableLiveTvManagement);
        var before = JsonSerializer.Serialize(config);
        ConfigurationMigration.NormalizeProvider(config);
        Assert.Equal(before, JsonSerializer.Serialize(config));
    }

    [Fact]
    public void ApiSettingsAddToExistingRulesWithoutForcingDefaults()
    {
        var config = Config();
        config.PermissionDefaults!["EnableLiveTvAccess"] = true;
        config.GroupPermissions.Add(new() { Role = "admins", LibraryMode = "All" });
        config.AdminRoles = ["admins"]; config.EnableFolderRoles = true;
        config.FolderRoleMapping = [new() { Role = "admins", Folders = ["films"] }];
        ConfigurationMigration.NormalizeProvider(config);
        Assert.True(config.PermissionDefaults["EnableLiveTvAccess"]);
        Assert.False(config.PermissionDefaults["IsAdministrator"]);
        var admins = Assert.Single(config.GroupPermissions);
        Assert.True(admins.Permissions["IsAdministrator"]); Assert.Equal("All", admins.LibraryMode);
    }

    // Randomized release-era configurations must grant exactly what v4.0.0.4 granted, for every role combination.
    [Fact]
    public void ConvertedReleaseSettingsMatchReleaseBehavior()
    {
        string[] pool = ["a", "b", "c", "d"];
        string[] libraries = ["films", "music", "kids"];
        var random = new Random(4004);
        T[] Some<T>(T[] from) => from.Where(_ => random.Next(3) == 0).ToArray();
        for (var sample = 0; sample < 500; sample++)
        {
            var release = new OidConfig
            {
                Enabled = true, EnableAuthorization = true, Roles = [],
                AdminRoles = Some(pool), EnableAllFolders = random.Next(4) == 0, EnabledFolders = Some(libraries),
                EnableFolderRoles = random.Next(2) == 0,
                FolderRoleMapping = Some(pool).Select(r => new FolderRoleMap { Role = r, Folders = [.. Some(libraries)] }).ToList(),
                EnableLiveTv = random.Next(3) == 0, EnableLiveTvManagement = random.Next(3) == 0, EnableLiveTvRoles = random.Next(2) == 0,
                LiveTvRoles = Some(pool), LiveTvManagementRoles = Some(pool),
            };
            var converted = ConfigurationMigration.Clone(release);
            converted.PermissionDefaults = null;
            ConfigurationMigration.NormalizeProvider(converted);
            for (var mask = 0; mask < 1 << pool.Length; mask++)
            {
                var roles = pool.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
                var expected = Release(release, roles);
                var actual = PermissionPolicy.Resolve(converted, roles, null);
                Assert.Equal(
                    new[] { PermissionKind.EnableAllFolders, PermissionKind.EnableLiveTvAccess, PermissionKind.EnableLiveTvManagement, PermissionKind.IsAdministrator }.Order(),
                    actual.Values.Keys.Order());
                Assert.Equal(expected.Admin, actual.Values[PermissionKind.IsAdministrator]);
                Assert.Equal(expected.All, actual.Values[PermissionKind.EnableAllFolders]);
                Assert.Equal(expected.LiveTv, actual.Values[PermissionKind.EnableLiveTvAccess]);
                Assert.Equal(expected.LiveTvManagement, actual.Values[PermissionKind.EnableLiveTvManagement]);
                if (!expected.All)
                {
                    Assert.Equal(expected.Folders.Distinct().Order(), actual.Folders.Order());
                }
            }
        }
    }

    // The v4.0.0.4 login policy, as computed by SSOController before Authenticate applied it.
    private static (bool Admin, bool All, string[] Folders, bool LiveTv, bool LiveTvManagement) Release(ProviderConfig config, string[] roles)
    {
        var folders = config.EnableFolderRoles ? [] : config.EnabledFolders.ToList();
        bool admin = false, liveTv = config.EnableLiveTv, liveTvManagement = config.EnableLiveTvManagement;
        foreach (var role in roles)
        {
            admin |= config.AdminRoles.Contains(role);
            if (config.EnableFolderRoles)
            {
                folders.AddRange(config.FolderRoleMapping.Where(m => m.Role.Trim() == role).SelectMany(m => m.Folders));
            }

            if (config.EnableLiveTvRoles)
            {
                liveTv |= config.LiveTvRoles.Contains(role);
                liveTvManagement |= config.LiveTvManagementRoles.Contains(role);
            }
        }

        return (admin, config.EnableAllFolders, folders.ToArray(), liveTv, liveTvManagement);
    }

    [Fact]
    public void OptOutWins()
    {
        var config = Config(); var id = Guid.NewGuid();
        config.UserPermissions.Add(new() { UserId = id, PreservePermissions = true });
        Assert.False(PermissionPolicy.Resolve(config, [], id).Synchronize);
        config.UserPermissions.Clear(); config.EnableAuthorization = false;
        Assert.False(PermissionPolicy.Resolve(config, [], id).Synchronize);
    }

    [Theory]
    [InlineData("IsDisabled")]
    [InlineData("EnableAllFolders")]
    [InlineData("NonexistentPermission")]
    public void UnsupportedOrHostOwnedPermissionsAreRejected(string permission)
    {
        var config = Config(); config.PermissionDefaults![permission] = true;
        Assert.Throws<SsoException>(() => ConfigurationMigration.NormalizeProvider(config));
    }

    [Fact]
    public void AmbiguousRulesAreRejectedAndSnapshotsDoNotInvalidateLogin()
    {
        var config = Config(); config.GroupPermissions = [new() { Role = "family" }, new() { Role = "family" }];
        Assert.Throws<SsoException>(() => ConfigurationMigration.NormalizeProvider(config));
        config.GroupPermissions.Clear(); var id = Guid.NewGuid();
        config.UserPermissions = [new() { UserId = id }, new() { UserId = id }];
        Assert.Throws<SsoException>(() => ConfigurationMigration.NormalizeProvider(config));
        config.UserPermissions.Clear(); config.GroupPermissions = [new() { Role = "family", LibraryMode = "invalid" }];
        Assert.Throws<SsoException>(() => ConfigurationMigration.NormalizeProvider(config));
        config.GroupPermissions.Clear();
        var fingerprint = ConfigurationMigration.Fingerprint(config);
        config.UserRoleSnapshots[id.ToString("N")] = new() { Roles = ["family"], ObservedAt = DateTimeOffset.UtcNow };
        Assert.Equal(fingerprint, ConfigurationMigration.Fingerprint(config));
    }

    [Fact]
    public void PreviewListsManagedAndUnmanagedValuesWithoutMutatingUser()
    {
        var user = new User("local", "local", "reset"); user.SetPermission(PermissionKind.EnableContentDeletion, true);
        var config = Config(); config.PermissionDefaults!["EnableContentDeletion"] = false;
        var rows = PermissionPolicy.Describe(PermissionPolicy.Resolve(config, [], user.Id), user);
        var deletion = rows.Single(p => p.Key == "EnableContentDeletion");
        Assert.True(deletion.Current); Assert.False(deletion.Effective); Assert.True(deletion.Managed);
        Assert.Equal("Everyone", deletion.Source); Assert.Equal("Off", deletion.Change);
        Assert.True(user.HasPermission(PermissionKind.EnableContentDeletion));
        Assert.False(rows.Single(p => p.Key == "IsDisabled").Managed);
        Assert.False(rows.Single(p => p.Key == "IsAdministrator").Managed);
    }
}

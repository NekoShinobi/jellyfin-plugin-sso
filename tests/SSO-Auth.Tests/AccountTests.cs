using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SSO_Auth.Api;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Services;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using ImageInfo = Jellyfin.Database.Implementations.Entities.ImageInfo;

namespace SSO_Auth.Tests;

public class AccountTests
{
    [Fact]
    public async Task SettingsChangesInvalidateProofsAndStaleLinksCanBeRepairedWithoutReplacingUsers()
    {
        var f = new Fixture();
        var completion = f.Completion();
        f.Config.OidConfigs["test"].Roles = ["different-policy"];
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(completion, f.Client, "127.0.0.1"));
        Assert.Empty(f.All);
        f.Config.OidConfigs["test"].Roles = ["allowed"];
        f.Config.OidConfigs["test"].SubjectLinks[f.Identity.Key] = Guid.NewGuid();
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(f.Completion(), f.Client, "127.0.0.1"));
        var recovered = f.AddUser("existing-recovered-user");
        recovered.SetPermission(PermissionKind.EnableContentDeletion, true);
        f.Store.Edit(c => c.OidConfigs["test"].SubjectLinks[f.Identity.Key] = recovered.Id);
        await f.Service.Login(f.Completion(), f.Client, "127.0.0.1");
        Assert.Same(recovered, Assert.Single(f.All));
        Assert.True(recovered.HasPermission(PermissionKind.EnableContentDeletion));
    }

    [Theory]
    [InlineData("OID")]
    [InlineData("SAML")]
    public async Task ProviderChangesDuringAccountUpdateStillPreventSessionIssuance(string mode)
    {
        var f = new Fixture();
        f.AddUser(f.Identity.DisplayName);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Users.Setup(u => u.UpdateUserAsync(It.IsAny<User>())).Returns(async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        var login = f.Service.Login(f.Completion(mode: mode), f.Client, "127.0.0.1");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Store.Edit(c => ProviderStore.Find(c, mode, "test").Roles = ["changed"]);
        }
        finally
        {
            release.TrySetResult();
        }
        await Assert.ThrowsAsync<SsoException>(() => login);
        Assert.Empty(f.Store.Get(mode, "test").SubjectLinks);
        f.Sessions.Verify(s => s.AuthenticateDirect(It.IsAny<AuthenticationRequest>()), Times.Never);
    }

    [Fact]
    public async Task AnExplicitlyLinkedSecondProviderUsesItsOwnAdmissionPolicy()
    {
        var f = new Fixture();
        var user = f.AddUser("existing-local-user");
        await f.Service.Link(f.Completion(user.Id), user.Id);
        var second = new OidConfig { Enabled = true, Roles = ["second-provider-role"] };
        ConfigurationMigration.NormalizeProvider(second);
        f.Config.OidConfigs["second"] = second;
        var identity = f.Identity with { Issuer = "https://second-issuer", Roles = ["second-provider-role"] };
        var tx = new LoginTransaction("OID", "second", "https://jf/callback", "browser", user.Id, ConfigurationMigration.Fingerprint(second), second);
        await f.Service.Link(new(tx, identity), user.Id);
        await f.Service.Login(new(tx with { TargetUser = null }, identity), f.Client, "127.0.0.1");
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(new(tx with { TargetUser = null }, identity with { Roles = ["allowed"] }), f.Client, "127.0.0.1"));
        Assert.Single(f.All);
        Assert.Equal(user.Id, f.Config.OidConfigs["test"].SubjectLinks[f.Identity.Key]);
        Assert.Equal(user.Id, f.Config.OidConfigs["second"].SubjectLinks[identity.Key]);
    }

    [Fact]
    public async Task SignInAndLinkingRecordDisplayDetailsForTheAccountPage()
    {
        var f = new Fixture(); var user = f.AddUser("existing-local-user");
        await f.Service.Link(f.Completion(user.Id), user.Id);
        var detail = f.Config.OidConfigs["test"].SubjectLinkDetails[f.Identity.Key];
        Assert.Equal(f.Identity.DisplayName, detail.Username);
        Assert.Equal(f.Identity.Issuer, detail.Issuer);
        Assert.NotNull(detail.LinkedAt); Assert.Null(detail.LastSignInAt);
        var linkedAt = detail.LinkedAt;
        await f.Service.Login(f.Completion(), f.Client, "127.0.0.1");
        detail = f.Config.OidConfigs["test"].SubjectLinkDetails[f.Identity.Key];
        Assert.Equal(linkedAt, detail.LinkedAt); Assert.NotNull(detail.LastSignInAt);
        // Details never outlive their link.
        f.Config.OidConfigs["test"].SubjectLinks.Clear();
        ConfigurationMigration.NormalizeProvider(f.Config.OidConfigs["test"]);
        Assert.Empty(f.Config.OidConfigs["test"].SubjectLinkDetails);
    }

    [Fact]
    public void MenuScriptIsAddedToTheWebClientOnce()
    {
        var index = "<html><head><title>Jellyfin</title></head><body></body></html>";
        var once = WebMenuIntegration.AddMenuScript(new WebFileContents { Contents = index });
        Assert.Contains("<script defer src=\"../SSOViews/menu.js\"></script></head>", once);
        Assert.Equal(once, WebMenuIntegration.AddMenuScript(new WebFileContents { Contents = once }));
        Assert.Equal("no head", WebMenuIntegration.AddMenuScript(new WebFileContents { Contents = "no head" }));
    }

    private sealed class Crypto : ICryptoProvider
    {
        public string DefaultHashMethod => "SHA256";
        public PasswordHash CreatePasswordHash(ReadOnlySpan<char> password) => new("SHA256", SHA256.HashData(Encoding.UTF8.GetBytes(password.ToString())));
        public bool Verify(PasswordHash hash, ReadOnlySpan<char> password) => false;
        public byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(16);
        public byte[] GenerateSalt(int length) => RandomNumberGenerator.GetBytes(length);
    }

    private sealed class Fixture
    {
        public PluginConfiguration Config = new();
        public readonly Mock<IUserManager> Users = new();
        public readonly Mock<ISessionManager> Sessions = new();
        public readonly Mock<INetworkManager> Network = new();
        public readonly Mock<IAvatarService> Avatars = new();
        public readonly List<User> All = [];
        public readonly ProviderStore Store;
        public readonly AccountService Service;
        public readonly ExternalIdentity Identity = new("https://issuer", "subject", "external-name", ["allowed"]);
        public readonly AuthResponse Client = new() { DeviceID = "device", DeviceName = "browser", AppName = "test", AppVersion = "1" };
        public Fixture()
        {
            Config.OidConfigs["test"] = new OidConfig { Enabled = true, Roles = ["allowed"] };
            Config.SamlConfigs["test"] = new SamlConfig { Enabled = true, Roles = ["allowed"] };
            Store = new(() => Config, c => Config = c);
            Users.Setup(u => u.GetAuthenticationProviders()).Returns([new NameIdPair { Id = "local", Name = "Local" }, new NameIdPair { Id = "ldap", Name = "LDAP" }]);
            Users.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => All.SingleOrDefault(u => u.Id == id));
            Users.Setup(u => u.GetUserByName(It.IsAny<string>())).Returns((string name) => All.SingleOrDefault(u => u.Username == name));
            Users.Setup(u => u.CreateUserAsync(It.IsAny<string>())).ReturnsAsync((string name) => AddUser(name));
            Users.Setup(u => u.UpdateUserAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
            Sessions.Setup(s => s.AuthenticateDirect(It.IsAny<AuthenticationRequest>())).ReturnsAsync(new AuthenticationResult());
            Network.Setup(n => n.IsInLocalNetwork(It.IsAny<string>())).Returns(true);
            Avatars.Setup(a => a.Refresh(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<Func<string, Task>>())).Returns(Task.CompletedTask);
            Service = new(Store, Users.Object, Sessions.Object, new Crypto(), Network.Object, Avatars.Object);
        }
        public User AddUser(string name)
        {
            var user = new User(name, "local", "reset");
            user.SetPermission(PermissionKind.EnableRemoteAccess, true);
            All.Add(user); return user;
        }
        public LoginCompletion Completion(Guid? target = null, ExternalIdentity? identity = null, string mode = "OID")
        {
            var settings = Store.Get(mode, "test");
            return new(new(mode, "test", "https://jf/callback", "browser", target, ConfigurationMigration.Fingerprint(settings), settings), identity ?? Identity);
        }
    }

    [Theory]
    [InlineData("OID")]
    [InlineData("SAML")]
    public async Task SuccessfulSignInRepairsLegacyAvatarWithoutAnAvatarUrl(string mode)
    {
        var f = new Fixture(); var user = f.AddUser(f.Identity.DisplayName);
        const string original = "/synthetic/user/profilepng";
        const string repaired = "/synthetic/user/profile-recovered.png";
        user.ProfileImage = new ImageInfo(original);
        f.Avatars.Setup(a => a.RepairLegacy(user.Id, original, It.IsAny<Func<string, Task<bool>>>()))
            .Returns(async (Guid id, string? path, Func<string, Task<bool>> save) => Assert.True(await save(repaired)));
        await f.Service.Login(f.Completion(mode: mode), f.Client, "127.0.0.1");
        Assert.Equal(repaired, user.ProfileImage.Path);
        f.Avatars.Verify(a => a.RepairLegacy(user.Id, original, It.IsAny<Func<string, Task<bool>>>()), Times.Once);
        f.Avatars.Verify(a => a.Refresh(user.Id, null, It.IsAny<Func<string, Task>>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyAvatarRepairCannotRestoreDeletedAccountsOrReplaceNewerAvatars(bool deleted)
    {
        var f = new Fixture(); var user = f.AddUser(f.Identity.DisplayName);
        user.ProfileImage = new ImageInfo("/synthetic/user/profilepng");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Avatars.Setup(a => a.RepairLegacy(user.Id, user.ProfileImage.Path, It.IsAny<Func<string, Task<bool>>>()))
            .Returns(async (Guid id, string? path, Func<string, Task<bool>> save) =>
            {
                entered.SetResult();
                await release.Task;
                Assert.False(await save("/synthetic/user/profile-recovered.png"));
            });
        var login = f.Service.Login(f.Completion(), f.Client, "127.0.0.1");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (deleted) f.All.Remove(user);
            else user.ProfileImage = new ImageInfo("/synthetic/manual-avatar.png");
            f.Users.Invocations.Clear();
            release.SetResult();
            await login.WaitAsync(TimeSpan.FromSeconds(5));
            f.Users.Verify(u => u.UpdateUserAsync(It.IsAny<User>()), Times.Never);
            if (!deleted) Assert.Equal("/synthetic/manual-avatar.png", user.ProfileImage.Path);
        }
        finally
        {
            release.TrySetResult();
            await login;
        }
    }

    [Fact]
    public async Task FailedAvatarMetadataSaveRestoresTheOriginalInMemoryReference()
    {
        var f = new Fixture(); var user = f.AddUser(f.Identity.DisplayName);
        var original = new ImageInfo("/synthetic/user/profilepng");
        user.ProfileImage = original;
        f.Users.Setup(u => u.UpdateUserAsync(It.Is<User>(u => u.ProfileImage != null && u.ProfileImage.Path.EndsWith(".png"))))
            .ThrowsAsync(new IOException("Synthetic metadata failure"));
        f.Avatars.Setup(a => a.RepairLegacy(user.Id, original.Path, It.IsAny<Func<string, Task<bool>>>()))
            .Returns(async (Guid id, string? path, Func<string, Task<bool>> save) =>
            {
                await Assert.ThrowsAsync<IOException>(() => save("/synthetic/user/profile-recovered.png"));
            });
        await f.Service.Login(f.Completion(), f.Client, "127.0.0.1");
        Assert.Same(original, user.ProfileImage);
        f.Avatars.Verify(a => a.Refresh(user.Id, null, It.IsAny<Func<string, Task>>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowAvatarDoesNotBlockAccountsAndNeverPersistsAnOldUserSnapshot(bool deleted)
    {
        var f = new Fixture();
        var original = f.AddUser(f.Identity.DisplayName);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Avatars.Setup(a => a.Refresh(original.Id, "https://avatar.test/slow", It.IsAny<Func<string, Task>>()))
            .Returns(async (Guid id, string? url, Func<string, Task> save) =>
            {
                entered.SetResult();
                await release.Task;
                await save("/synthetic/profile.png");
            });
        var slow = f.Service.Login(f.Completion(identity: f.Identity with { AvatarUrl = "https://avatar.test/slow" }), f.Client, "127.0.0.1");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var other = f.Identity with { Subject = "other", DisplayName = "other" };
            await f.Service.Login(f.Completion(identity: other), f.Client, "127.0.0.1").WaitAsync(TimeSpan.FromSeconds(5));
            var otherUser = f.All.Single(u => u.Username == "other");
            await f.Service.Link(f.Completion(otherUser.Id, other with { Subject = "linked" }), otherUser.Id).WaitAsync(TimeSpan.FromSeconds(5));
            await f.Service.Unregister("other", "ldap").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(slow.IsCompleted);

            // Host reads return detached records: simulate a newer account snapshot during the download.
            var current = new User("updated-name", "ldap", "reset") { Id = original.Id };
            current.SetPermission(PermissionKind.EnableContentDownloading, true);
            f.All.Remove(original);
            if (!deleted) f.All.Add(current);
            f.Users.Invocations.Clear();
            release.SetResult();
            await slow.WaitAsync(TimeSpan.FromSeconds(5));
            f.Users.Verify(u => u.UpdateUserAsync(original), Times.Never);
            if (deleted)
            {
                f.Users.Verify(u => u.UpdateUserAsync(It.IsAny<User>()), Times.Never);
            }
            else
            {
                f.Users.Verify(u => u.UpdateUserAsync(current), Times.Once);
                Assert.Equal("/synthetic/profile.png", current.ProfileImage!.Path);
                Assert.Equal("ldap", current.AuthenticationProviderId);
                Assert.True(current.HasPermission(PermissionKind.EnableContentDownloading));
            }
        }
        finally
        {
            release.TrySetResult();
            await slow;
        }
    }

    [Theory]
    [InlineData("OID")]
    [InlineData("SAML")]
    public async Task InvalidUsernameExplainsRecoveryWithoutCreatingLinksOrSessions(string mode)
    {
        var f = new Fixture();
        var identity = f.Identity with { DisplayName = "invalid/name" };
        f.Users.Setup(u => u.CreateUserAsync(identity.DisplayName))
            .ThrowsAsync(new ArgumentException("Host validation details", "name"));

        var error = await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(f.Completion(identity: identity, mode: mode), f.Client, "127.0.0.1"));

        Assert.Equal(400, error.Status);
        Assert.Equal("The identity provider returned a username that Jellyfin does not accept. Ask your administrator to configure a compatible username claim.", error.Message);
        Assert.Empty(f.All);
        Assert.Empty(f.Store.Get(mode, "test").SubjectLinks);
        f.Users.Verify(u => u.UpdateUserAsync(It.IsAny<User>()), Times.Never);
        f.Sessions.Verify(s => s.AuthenticateDirect(It.IsAny<AuthenticationRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other")]
    public async Task UnrelatedAccountCreationErrorsAreNotReportedAsInvalidUsernames(string? parameter)
    {
        var f = new Fixture();
        var failure = new ArgumentException("An unrelated creation failure", parameter);
        f.Users.Setup(u => u.CreateUserAsync(It.IsAny<string>())).ThrowsAsync(failure);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => f.Service.Login(f.Completion(), f.Client, "127.0.0.1"));

        Assert.Same(failure, error);
        Assert.Empty(f.Store.Get("OID", "test").SubjectLinks);
        f.Sessions.Verify(s => s.AuthenticateDirect(It.IsAny<AuthenticationRequest>()), Times.Never);
    }

    [Fact]
    public async Task SimultaneousLoginsCreateOneUserAndPreserveStableGuidAfterRename()
    {
        var f = new Fixture();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => f.Service.Login(f.Completion(), f.Client, "192.0.2.4")));
        var user = Assert.Single(f.All); var id = user.Id;
        Assert.Equal(AccountService.ProviderId, user.AuthenticationProviderId);
        Assert.False(string.IsNullOrWhiteSpace(user.Password));
        user.Username = "local-only-rename";
        await f.Service.Login(f.Completion(identity: f.Identity with { DisplayName = "external-rename" }), f.Client, "192.0.2.4");
        Assert.Equal(id, Assert.Single(f.All).Id);
        f.Sessions.Verify(s => s.AuthenticateDirect(It.Is<AuthenticationRequest>(r => r.RemoteEndPoint == "192.0.2.4")), Times.Exactly(17));
    }

    [Fact]
    public async Task StaleLinksNeverFallBackToMatchingNamesOrProvisionReplacementUsers()
    {
        var f = new Fixture(); var user = f.AddUser(f.Identity.DisplayName);
        f.Config.OidConfigs["test"].CanonicalLinks[f.Identity.DisplayName] = Guid.NewGuid();
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(f.Completion(), f.Client, "127.0.0.1"));
        f.Config.OidConfigs["test"].CanonicalLinks[f.Identity.DisplayName] = user.Id;
        f.Config.OidConfigs["test"].SubjectLinks[f.Identity.Key] = Guid.NewGuid();
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(f.Completion(), f.Client, "127.0.0.1"));
        f.All.Clear(); f.Config.OidConfigs["test"].SubjectLinks.Clear();
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(f.Completion(), f.Client, "127.0.0.1"));
        f.Config.OidConfigs["test"].SubjectLinks[f.Identity.Key] = user.Id;
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(f.Completion(), f.Client, "127.0.0.1"));
        Assert.Empty(f.All);
        f.Sessions.Verify(s => s.AuthenticateDirect(It.IsAny<AuthenticationRequest>()), Times.Never);
    }

    [Theory]
    [InlineData("OID")]
    [InlineData("SAML")]
    public async Task FirstLoginMatchesExistingUsernameAndKeepsCredentialsAndGuidAfterRename(string mode)
    {
        var f = new Fixture(); var user = f.AddUser(f.Identity.DisplayName);
        user.Password = "existing-password-hash";
        user.SetPermission(PermissionKind.EnableContentDeletion, true);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => f.Service.Login(f.Completion(mode: mode), f.Client, "127.0.0.1")));
        Assert.Same(user, Assert.Single(f.All));
        Assert.Equal("existing-password-hash", user.Password);
        Assert.Equal("local", user.AuthenticationProviderId);
        Assert.True(user.HasPermission(PermissionKind.EnableContentDeletion));
        Assert.Equal(user.Id, f.Store.Get(mode, "test").SubjectLinks[f.Identity.Key]);
        user.Username = "renamed-local-user";
        await f.Service.Login(f.Completion(identity: f.Identity with { DisplayName = "renamed-external-user" }, mode: mode), f.Client, "127.0.0.1");
        f.Users.Verify(u => u.CreateUserAsync(It.IsAny<string>()), Times.Never);
        f.Sessions.Verify(s => s.AuthenticateDirect(It.Is<AuthenticationRequest>(r => r.UserId == user.Id)), Times.Exactly(17));
    }

    [Theory]
    [InlineData("OID")]
    [InlineData("SAML")]
    public async Task SavedUsernameMappingTakesPrecedenceOverMatchingLocalName(string mode)
    {
        var f = new Fixture(); var linked = f.AddUser("renamed-local-user");
        var sameName = f.AddUser(f.Identity.DisplayName);
        f.Store.Edit(c => ProviderStore.Find(c, mode, "test").CanonicalLinks[f.Identity.DisplayName] = linked.Id);
        await f.Service.Login(f.Completion(mode: mode), f.Client, "127.0.0.1");
        Assert.Equal(linked.Id, f.Store.Get(mode, "test").SubjectLinks[f.Identity.Key]);
        Assert.Equal(linked.Id, f.Store.Get(mode, "test").CanonicalLinks[f.Identity.DisplayName]);
        // Stable identity wins if its external username later matches another saved link.
        f.Store.Edit(c => ProviderStore.Find(c, mode, "test").CanonicalLinks["changed-name"] = sameName.Id);
        await f.Service.Login(f.Completion(identity: f.Identity with { DisplayName = "changed-name" }, mode: mode), f.Client, "127.0.0.1");
        f.Users.Verify(u => u.CreateUserAsync(It.IsAny<string>()), Times.Never);
        f.Sessions.Verify(s => s.AuthenticateDirect(It.Is<AuthenticationRequest>(r => r.UserId == linked.Id)), Times.Exactly(2));
    }

    [Theory]
    [InlineData("legacy-3.xml", "Legacy-OIDC", "legacy-oidc-user")]
    [InlineData("legacy-4.xml", "Case-Sensitive", "legacy-name")]
    public async Task HistoricalUsernameLinksSignInWithoutRelinking(string file, string providerName, string oldName)
    {
        var f = new Fixture();
        using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        var imported = ConfigurationMigration.Normalize((PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(input)!);
        var provider = imported.OidConfigs[providerName];
        provider.Enabled = true;
        f.Config.OidConfigs["test"] = provider;
        var user = f.AddUser("local-name-changed-since-original-link");
        user.Id = provider.CanonicalLinks[oldName];
        user.AuthenticationProviderId = AccountService.LegacyProvider;
        var identity = f.Identity with { DisplayName = oldName, Roles = ["media"] };
        await f.Service.Login(f.Completion(identity: identity), f.Client, "127.0.0.1");
        Assert.Same(user, Assert.Single(f.All));
        Assert.Equal(AccountService.ProviderId, user.AuthenticationProviderId);
        Assert.Equal(user.Id, f.Config.OidConfigs["test"].SubjectLinks[identity.Key]);
        Assert.Equal(user.Id, f.Config.OidConfigs["test"].CanonicalLinks[oldName]);
        f.Users.Verify(u => u.CreateUserAsync(It.IsAny<string>()), Times.Never);
        f.Sessions.Verify(s => s.AuthenticateDirect(It.Is<AuthenticationRequest>(r => r.UserId == user.Id)), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsernameMatchingStillRequiresProviderAdmissionAndHostPolicy(bool legacy)
    {
        var f = new Fixture(); var user = f.AddUser(f.Identity.DisplayName);
        if (legacy) f.Config.OidConfigs["test"].CanonicalLinks[f.Identity.DisplayName] = user.Id;
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(f.Completion(identity: f.Identity with { Roles = [] }), f.Client, "127.0.0.1"));
        user.SetPermission(PermissionKind.IsDisabled, true);
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(f.Completion(), f.Client, "127.0.0.1"));
        user.SetPermission(PermissionKind.IsDisabled, false);
        user.SetPermission(PermissionKind.EnableRemoteAccess, false);
        f.Network.Setup(n => n.IsInLocalNetwork(It.IsAny<string>())).Returns(false);
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(f.Completion(), f.Client, "203.0.113.2"));
        Assert.Empty(f.Config.OidConfigs["test"].SubjectLinks);
        f.Users.Verify(u => u.CreateUserAsync(It.IsAny<string>()), Times.Never);
        f.Sessions.Verify(s => s.AuthenticateDirect(It.IsAny<AuthenticationRequest>()), Times.Never);
    }

    [Fact]
    public async Task LinkingRequiresTargetProofAndRejectsConflictsWithoutIssuingSessions()
    {
        var f = new Fixture(); var user = f.AddUser("local-name"); var other = f.AddUser("other");
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Link(f.Completion(user.Id), other.Id));
        await f.Service.Link(f.Completion(user.Id), user.Id);
        Assert.Equal(user.Id, f.Config.OidConfigs["test"].SubjectLinks[f.Identity.Key]);
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Link(f.Completion(other.Id), other.Id));
        Assert.Equal(2, f.All.Count);
        f.Sessions.Verify(s => s.AuthenticateDirect(It.IsAny<AuthenticationRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyOptOutPreservesManagedFieldsAndSynchronizationRevokesRemovedRoles(bool synchronize)
    {
        var f = new Fixture(); var user = f.AddUser("local");
        f.Config.OidConfigs["test"].SubjectLinks[f.Identity.Key] = user.Id;
        f.Config.OidConfigs["test"].EnableAuthorization = synchronize;
        foreach (var permission in new[] { PermissionKind.IsAdministrator, PermissionKind.EnableAllFolders, PermissionKind.EnableLiveTvAccess, PermissionKind.EnableLiveTvManagement }) user.SetPermission(permission, true);
        user.SetPermission(PermissionKind.EnableContentDeletion, true);
        user.SetPreference(PreferenceKind.EnabledFolders, ["folder"]);
        await f.Service.Login(f.Completion(), f.Client, "127.0.0.1");
        foreach (var permission in new[] { PermissionKind.IsAdministrator, PermissionKind.EnableAllFolders, PermissionKind.EnableLiveTvAccess, PermissionKind.EnableLiveTvManagement }) Assert.Equal(!synchronize, user.HasPermission(permission));
        Assert.True(user.HasPermission(PermissionKind.EnableContentDeletion));
    }

    [Fact]
    public async Task DisabledAndRemoteRestrictedUsersCannotReceiveCredentials()
    {
        var f = new Fixture(); var user = f.AddUser("local"); f.Config.OidConfigs["test"].SubjectLinks[f.Identity.Key] = user.Id;
        user.SetPermission(PermissionKind.IsDisabled, true);
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(f.Completion(), f.Client, "203.0.113.2"));
        user.SetPermission(PermissionKind.IsDisabled, false); user.SetPermission(PermissionKind.EnableRemoteAccess, false);
        f.Network.Setup(n => n.IsInLocalNetwork(It.IsAny<string>())).Returns(false);
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(f.Completion(), f.Client, "203.0.113.2"));
        f.Sessions.Verify(s => s.AuthenticateDirect(It.IsAny<AuthenticationRequest>()), Times.Never);
    }

    [Fact]
    public async Task LoginMatchesPreviewAndRevokesPermissionsAfterGroupsChange()
    {
        var f = new Fixture(); var user = f.AddUser(f.Identity.DisplayName);
        f.Config.OidConfigs["test"].EnableAuthorization = true;
        f.Config.OidConfigs["test"].GroupPermissions.Add(new() { Role = "allowed", Permissions = new() { ["EnableContentDownloading"] = true, ["EnableContentDeletion"] = true } });
        f.Config.OidConfigs["test"].UserPermissions.Add(new() { UserId = user.Id, Permissions = new() { ["EnableContentDeletion"] = false } });
        var preview = JsonSerializer.SerializeToElement(f.Service.PreviewPermissions(new() { Configuration = f.Config.OidConfigs["test"], UserId = user.Id, Roles = ["allowed"] }));
        Assert.Empty(f.Config.OidConfigs["test"].SubjectLinks);
        Assert.Empty(f.Config.OidConfigs["test"].UserRoleSnapshots);
        f.Users.Verify(u => u.UpdateUserAsync(It.IsAny<User>()), Times.Never);
        await f.Service.Login(f.Completion(), f.Client, "127.0.0.1");
        foreach (var row in preview.GetProperty("Permissions").EnumerateArray().Where(r => r.GetProperty("Managed").GetBoolean()))
            Assert.Equal(row.GetProperty("Effective").GetBoolean(), user.HasPermission(Enum.Parse<PermissionKind>(row.GetProperty("Key").GetString()!)));
        Assert.True(user.HasPermission(PermissionKind.EnableContentDownloading));
        Assert.False(user.HasPermission(PermissionKind.EnableContentDeletion));
        Assert.Equal(new[] { "allowed" }, f.Config.OidConfigs["test"].UserRoleSnapshots[user.Id.ToString("N")].Roles);
        f.Config.OidConfigs["test"].Roles = [];
        await f.Service.Login(f.Completion(identity: f.Identity with { Roles = [] }), f.Client, "127.0.0.1");
        Assert.False(user.HasPermission(PermissionKind.EnableContentDownloading));
        Assert.Empty(f.Config.OidConfigs["test"].UserRoleSnapshots[user.Id.ToString("N")].Roles);
    }

    [Fact]
    public async Task PerUserExemptionPreservesPermissionsAndBlockedPreviewMakesNoChanges()
    {
        var f = new Fixture(); var user = f.AddUser(f.Identity.DisplayName);
        user.SetPermission(PermissionKind.EnableContentDeletion, true);
        f.Config.OidConfigs["test"].EnableAuthorization = true;
        f.Config.OidConfigs["test"].PermissionDefaults = new() { ["EnableContentDeletion"] = false };
        f.Config.OidConfigs["test"].UserPermissions.Add(new() { UserId = user.Id, PreservePermissions = true });
        await f.Service.Login(f.Completion(), f.Client, "127.0.0.1");
        Assert.True(user.HasPermission(PermissionKind.EnableContentDeletion));
        user.SetPermission(PermissionKind.IsDisabled, true);
        var preview = JsonSerializer.SerializeToElement(f.Service.PreviewPermissions(new() { Configuration = f.Config.OidConfigs["test"], UserId = user.Id, Roles = ["allowed"] }));
        Assert.False(preview.GetProperty("Admitted").GetBoolean());
        Assert.All(preview.GetProperty("Permissions").EnumerateArray(), row => Assert.False(row.GetProperty("Managed").GetBoolean()));
    }

    [Theory]
    [InlineData("  ldap  ", "ldap")]
    [InlineData("   ", "local")]
    [InlineData(null, "local")]
    [InlineData("  missing  ", null)]
    public async Task NormalizedFallbackIdsStillRequireARegisteredProvider(string? fallback, string? expected)
    {
        var f = new Fixture(); var user = f.AddUser(f.Identity.DisplayName);
        user.AuthenticationProviderId = "local";
        f.Config.OidConfigs["test"].DefaultProvider = fallback!;
        if (expected is null)
        {
            await Assert.ThrowsAsync<SsoException>(() => f.Service.Login(f.Completion(), f.Client, "127.0.0.1"));
            Assert.Equal("local", user.AuthenticationProviderId);
            f.Sessions.Verify(s => s.AuthenticateDirect(It.IsAny<AuthenticationRequest>()), Times.Never);
        }
        else
        {
            await f.Service.Login(f.Completion(), f.Client, "127.0.0.1");
            Assert.Equal(expected, user.AuthenticationProviderId);
        }
    }

    [Fact]
    public async Task RegisteredFallbacksRemainValidAndUnregisterPersists()
    {
        var f = new Fixture(); var user = f.AddUser("local"); f.Config.OidConfigs["test"].SubjectLinks[f.Identity.Key] = user.Id;
        f.Config.OidConfigs["test"].DefaultProvider = "ldap";
        await f.Service.Login(f.Completion(), f.Client, "127.0.0.1"); Assert.Equal("ldap", user.AuthenticationProviderId);
        await f.Service.Unregister("local", "local"); Assert.Equal("local", user.AuthenticationProviderId);
        await Assert.ThrowsAsync<SsoException>(() => f.Service.Unregister("local", "missing"));
    }
}

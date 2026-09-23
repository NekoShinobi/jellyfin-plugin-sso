#nullable enable
using System;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SSO_Auth.Api;
using Jellyfin.Plugin.SSO_Auth.Config;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Cryptography;

namespace Jellyfin.Plugin.SSO_Auth.Services;

public sealed class SsoAuthenticationProvider : IAuthenticationProvider
{
    public string Name => "SSO (browser sign-in)";

    public bool IsEnabled => true;

    public Task<ProviderAuthenticationResult> Authenticate(string username, string password) => throw new SecurityException("Use browser SSO or a configured fallback provider.");

    public Task ChangePassword(User user, string newPassword) => throw new SecurityException("Configure a password-capable fallback provider first.");
}

public sealed class AccountService(ProviderStore providers, IUserManager users, ISessionManager sessions, ICryptoProvider crypto, INetworkManager network, IAvatarService avatars)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public const string LegacyProvider = "Jellyfin.Plugin.SSO_Auth.Api.SSOController";

    public static string ProviderId => typeof(SsoAuthenticationProvider).FullName!;

    public async Task<AuthenticationResult> Login(LoginCompletion completion, AuthResponse request, string remoteAddress)
    {
        ValidateClient(request);
        User user;
        AuthenticationResult result;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var transaction = completion.Transaction;
            if (transaction.TargetUser is not null)
            {
                throw new SsoException("A linking proof cannot issue a login session.", 403);
            }

            var config = providers.GetUnchanged(transaction);
            IdentityPolicy.Admit(config, completion.Identity.Roles);
            ValidateFallback(config.DefaultProvider);
            var hasSubjectLink = config.SubjectLinks.TryGetValue(completion.Identity.Key, out var userId);
            if (hasSubjectLink)
            {
                user = users.GetUserById(userId) ?? throw new SsoException("The linked Jellyfin user no longer exists. An administrator must repair the link.", 409);
            }
            else if (config.CanonicalLinks.TryGetValue(completion.Identity.DisplayName, out var legacyUserId))
            {
                // Honor upstream mappings even when the local account has been renamed.
                user = users.GetUserById(legacyUserId) ?? throw new SsoException("The linked Jellyfin user no longer exists. An administrator must repair the link.", 409);
            }
            else
            {
                // Username matching intentionally trusts the configured identity provider's namespace.
                var existingUser = users.GetUserByName(completion.Identity.DisplayName);
                if (existingUser is not null)
                {
                    user = existingUser;
                }
                else
                {
                    try
                    {
                        user = await users.CreateUserAsync(completion.Identity.DisplayName).ConfigureAwait(false);
                    }
                    catch (ArgumentException exception) when (exception.ParamName == "name")
                    {
                        throw new SsoException("The identity provider returned a username that Jellyfin does not accept. Ask your administrator to configure a compatible username claim.");
                    }

                    user.AuthenticationProviderId = ProviderId;
                    user.Password = crypto.CreatePasswordHash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))).ToString();
                    await users.UpdateUserAsync(user).ConfigureAwait(false);
                }
            }

            CheckHostPolicy(user, remoteAddress);
            var policy = PermissionPolicy.Resolve(config, completion.Identity.Roles, user.Id);
            if (policy.Synchronize)
            {
                foreach (var permission in policy.Values)
                {
                    user.SetPermission(permission.Key, permission.Value);
                }

                // Like the release, keep the stored selection when every library is allowed.
                if (!policy.Values[PermissionKind.EnableAllFolders])
                {
                    user.SetPreference(PreferenceKind.EnabledFolders, policy.Folders);
                }
            }

            if (!string.IsNullOrWhiteSpace(config.DefaultProvider))
            {
                user.AuthenticationProviderId = config.DefaultProvider;
            }
            else if (user.AuthenticationProviderId == LegacyProvider)
            {
                user.AuthenticationProviderId = ProviderId;
            }

            await users.UpdateUserAsync(user).ConfigureAwait(false);
            providers.EnsureUnchanged(transaction);
            if (!hasSubjectLink)
            {
                providers.Edit(c => ProviderStore.Find(c, transaction.Mode, transaction.Provider).SubjectLinks.Add(completion.Identity.Key, user.Id));
            }

            result = await sessions.AuthenticateDirect(new AuthenticationRequest
            {
                UserId = user.Id,
                Username = user.Username,
                App = request.AppName,
                AppVersion = request.AppVersion,
                DeviceId = request.DeviceID,
                DeviceName = request.DeviceName,
                RemoteEndPoint = remoteAddress,
            }).ConfigureAwait(false);
            providers.Edit(c => ProviderStore.Find(c, transaction.Mode, transaction.Provider).UserRoleSnapshots[user.Id.ToString("N")] = new UserRoleSnapshot
            {
                Roles = completion.Identity.Roles.Distinct(StringComparer.Ordinal).ToArray(),
                ObservedAt = DateTimeOffset.UtcNow,
            });
        }
        finally
        {
            _gate.Release();
        }

        await avatars.Refresh(user.Id, completion.Identity.AvatarUrl, path => UpdateAvatar(user.Id, path)).ConfigureAwait(false);
        return result;
    }

    private async Task UpdateAvatar(Guid userId, string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A download can outlive another login or account edit. Never persist its old user snapshot.
            var current = users.GetUserById(userId);
            if (current is null)
            {
                return;
            }

            current.ProfileImage = new ImageInfo(path);
            await users.UpdateUserAsync(current).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task Link(LoginCompletion completion, Guid target)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var transaction = completion.Transaction;
            if (transaction.TargetUser != target || users.GetUserById(target) is null)
            {
                throw new SsoException("The linking target does not match the initiating account.", 403);
            }

            IdentityPolicy.Admit(providers.GetUnchanged(transaction), completion.Identity.Roles);
            providers.Edit(c =>
            {
                var provider = ProviderStore.Find(c, transaction.Mode, transaction.Provider);
                if (provider.SubjectLinks.TryGetValue(completion.Identity.Key, out var existing) && existing != target)
                {
                    throw new SsoException("This provider identity is already linked to a different Jellyfin account.", 409);
                }

                if (provider.CanonicalLinks.TryGetValue(completion.Identity.DisplayName, out var legacy) && legacy != target)
                {
                    throw new SsoException("An existing legacy link requires administrator review.", 409);
                }

                provider.SubjectLinks[completion.Identity.Key] = target;
                // Preserve existing username mappings for upstream compatibility.
            });
            var user = users.GetUserById(target)!;
            if (user.AuthenticationProviderId == LegacyProvider)
            {
                user.AuthenticationProviderId = ProviderId;
                await users.UpdateUserAsync(user).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task Unregister(string username, string provider)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ValidateFallback(provider, required: true);
            var user = users.GetUserByName(username) ?? throw new SsoException("User not found.", 404);
            user.AuthenticationProviderId = provider;
            await users.UpdateUserAsync(user).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public object PreviewPermissions(PermissionPreviewRequest request)
    {
        var config = ConfigurationMigration.Clone(request.Configuration ?? throw new SsoException("Provider configuration is required."));
        ConfigurationMigration.NormalizeProvider(config);
        var user = request.UserId is { } id ? users.GetUserById(id) ?? throw new SsoException("Jellyfin user not found.", 404) : null;
        var roles = (request.Roles ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal).ToArray();
        ResolvedPermissions? resolved = null;
        string? blocked = !config.Enabled ? "Provider is disabled" : null;
        try
        {
            resolved = PermissionPolicy.Resolve(config, roles, user?.Id);
        }
        catch (SsoException error) when (error.Status == 403)
        {
            blocked = error.Message;
        }

        if (user?.HasPermission(PermissionKind.IsDisabled) == true)
        {
            blocked = "Jellyfin account is disabled";
        }
        else if (user is not null && !user.IsParentalScheduleAllowed())
        {
            blocked = "Jellyfin access schedule currently blocks sign-in";
        }

        var synchronize = resolved?.Synchronize == true && blocked is null;
        var currentFolders = user?.GetPreference(PreferenceKind.EnabledFolders);
        return new
        {
            Roles = roles,
            UserId = user?.Id,
            Admitted = blocked is null,
            BlockedReason = blocked,
            Synchronize = synchronize,
            Permissions = PermissionPolicy.Describe(resolved, user, blocked),
            Libraries = new
            {
                Current = currentFolders,
                Effective = synchronize ? resolved!.Folders : currentFolders,
                All = synchronize ? (bool?)resolved!.Values[PermissionKind.EnableAllFolders] : user?.HasPermission(PermissionKind.EnableAllFolders),
                Managed = synchronize,
                Source = blocked ?? (synchronize ? resolved!.FolderSource : "Keep Jellyfin setting"),
            },
            Note = "Preview shows stored permission flags for the entered groups. Jellyfin administrator privileges can bypass individual restrictions. Device restrictions, remote access, schedules, channel and device lists, and playback limits remain subject to Jellyfin's own checks. Changes apply at the next successful sign-in, not immediately.",
        };
    }

    private void ValidateFallback(string? provider, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(provider) && !required)
        {
            return;
        }

        if (!users.GetAuthenticationProviders().Any(p => p.Id == provider))
        {
            throw new SsoException("The configured fallback authentication provider is not registered.");
        }
    }

    public void CheckHostPolicy(User user, string remoteAddress)
    {
        if (user.HasPermission(PermissionKind.IsDisabled) || !user.IsParentalScheduleAllowed()
            || (!user.HasPermission(PermissionKind.EnableRemoteAccess) && !network.IsInLocalNetwork(remoteAddress)))
        {
            throw new SsoException("Jellyfin's user policy does not permit this sign-in.", 403);
        }
    }

    private static void ValidateClient(AuthResponse request)
    {
        if (new[] { request.DeviceID, request.DeviceName, request.AppName, request.AppVersion }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 256))
        {
            throw new SsoException("Client identification is missing or too long.");
        }
    }
}

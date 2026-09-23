#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SSO_Auth.Config;

namespace Jellyfin.Plugin.SSO_Auth.Services;

public sealed record PermissionDefinition(string Key, string Name, string Category, bool Editable = true);

public sealed record ResolvedPermissions(bool Synchronize, Dictionary<PermissionKind, bool> Values, Dictionary<PermissionKind, string> Sources, string[] Folders, string FolderSource);

public sealed record PermissionPreviewRow(string Key, string Name, string Category, bool? Current, bool? Effective, bool Managed, string Change, string Source);

public sealed class PermissionPreviewRequest
{
    public ProviderConfig Configuration { get; set; } = new();

    public Guid? UserId { get; set; }

    public string[] Roles { get; set; } = [];
}

public static class PermissionPolicy
{
    public static readonly PermissionDefinition[] Catalogue =
    [
        new("IsAdministrator", "Administrator", "Account"),
        new("IsHidden", "Hide user from sign-in screens", "Account"),
        new("IsDisabled", "Account disabled", "Account", false),
        new("EnableRemoteAccess", "Remote access", "Access"),
        new("EnableAllDevices", "Access all devices", "Access"),
        new("EnableAllFolders", "Access all libraries", "Libraries", false),
        new("EnableMediaPlayback", "Media playback", "Playback"),
        new("EnableAudioPlaybackTranscoding", "Audio transcoding", "Playback"),
        new("EnableVideoPlaybackTranscoding", "Video transcoding", "Playback"),
        new("EnablePlaybackRemuxing", "Playback remuxing", "Playback"),
        new("ForceRemoteSourceTranscoding", "Force remote source transcoding", "Playback"),
        new("EnableContentDownloading", "Download media", "Media management"),
        new("EnableContentDeletion", "Delete media", "Media management"),
        new("EnableMediaConversion", "Convert media", "Media management"),
        new("EnableSyncTranscoding", "Download transcoding", "Media management"),
        new("EnableCollectionManagement", "Manage collections", "Media management"),
        new("EnableSubtitleManagement", "Manage subtitles", "Media management"),
        new("EnableLyricManagement", "Manage lyrics", "Media management"),
        new("EnableLiveTvAccess", "Live TV playback", "Live TV"),
        new("EnableLiveTvManagement", "Manage Live TV recordings", "Live TV"),
        new("EnableAllChannels", "Access all channels", "Live TV"),
        new("EnablePublicSharing", "Public sharing", "Sharing and control"),
        new("EnableSharedDeviceControl", "Control shared devices", "Sharing and control"),
        new("EnableRemoteControlOfOtherUsers", "Control other users", "Sharing and control"),
    ];

    public static void Normalize(ProviderConfig config)
    {
        var legacy = config.PermissionDefaults is null;
        config.PermissionDefaults ??= new();
        config.GroupPermissions ??= [];
        config.UserPermissions ??= [];
        config.UserRoleSnapshots ??= new();
        var allowed = Catalogue.Where(p => p.Editable).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        void ValidatePermissions(IEnumerable<string> keys)
        {
            if (keys.Any(k => !allowed.Contains(k)))
            {
                throw new SsoException("A permission rule contains an unsupported permission. Account disabling remains managed by Jellyfin; use library selections for library access.");
            }
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        var users = new HashSet<Guid>();
        foreach (var rule in config.GroupPermissions)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Role) || !roles.Add(rule.Role))
            {
                throw new SsoException("Group permission rules need unique, nonempty role names.");
            }
        }

        foreach (var rule in config.UserPermissions)
        {
            if (rule is null || rule.UserId == Guid.Empty || !users.Add(rule.UserId))
            {
                throw new SsoException("User permission overrides need unique Jellyfin user IDs.");
            }
        }

        foreach (var rule in config.GroupPermissions.Cast<PermissionRule>().Concat(config.UserPermissions))
        {
            rule.Permissions ??= new();
            rule.Folders ??= [];
            if (rule.LibraryMode is not ("Inherit" or "All" or "Selected"))
            {
                throw new SsoException("Library access must inherit, allow all libraries, or select libraries.");
            }
        }

        if (config.GroupPermissions.Any(r => r.Permissions.ContainsValue(false)))
        {
            throw new SsoException("Group rules can only grant permissions. Turn a permission off in the defaults for everyone or in a user override.");
        }

        ConvertLegacySettings(config, legacy);
        ValidatePermissions(config.PermissionDefaults.Keys);
        foreach (var rule in config.GroupPermissions.Cast<PermissionRule>().Concat(config.UserPermissions))
        {
            ValidatePermissions(rule.Permissions.Keys);
            // Keep a baseline even after a rule is removed, so its grants cannot linger.
            foreach (var key in rule.Permissions.Keys)
            {
                config.PermissionDefaults.TryAdd(key, false);
            }
        }
    }

    // Expresses release-era Access settings as defaults and group grants with the same result.
    private static void ConvertLegacySettings(ProviderConfig config, bool legacy)
    {
        var defaults = config.PermissionDefaults!;
        if (legacy)
        {
            // Release synchronization always wrote these flags, even when no role matched.
            defaults["IsAdministrator"] = false;
            defaults["EnableLiveTvAccess"] = config.EnableLiveTv;
            defaults["EnableLiveTvManagement"] = config.EnableLiveTvManagement;
        }
        else
        {
            if (config.EnableLiveTv)
            {
                defaults["EnableLiveTvAccess"] = true;
            }

            if (config.EnableLiveTvManagement)
            {
                defaults["EnableLiveTvManagement"] = true;
            }
        }

        GroupPermissionRule Group(string role)
        {
            var rule = config.GroupPermissions.FirstOrDefault(r => r.Role == role);
            if (rule is null)
            {
                rule = new GroupPermissionRule { Role = role };
                config.GroupPermissions.Add(rule);
            }

            return rule;
        }

        void Grant(IEnumerable<string> roles, string key)
        {
            foreach (var role in roles.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal))
            {
                Group(role).Permissions[key] = true;
            }
        }

        Grant(config.AdminRoles ?? [], "IsAdministrator");
        if (config.EnableLiveTvRoles)
        {
            Grant(config.LiveTvRoles ?? [], "EnableLiveTvAccess");
            Grant(config.LiveTvManagementRoles ?? [], "EnableLiveTvManagement");
        }

        if (config.EnableFolderRoles && !config.EnableAllFolders)
        {
            // Release mappings replaced the library list for everyone instead of adding to it.
            config.EnabledFolders = [];
            foreach (var mapping in config.FolderRoleMapping ?? [])
            {
                var role = mapping.Role?.Trim();
                if (string.IsNullOrEmpty(role))
                {
                    continue;
                }

                var rule = Group(role);
                if (rule.LibraryMode != "All")
                {
                    rule.LibraryMode = "Selected";
                    rule.Folders = rule.Folders.Concat(mapping.Folders ?? []).Distinct(StringComparer.Ordinal).ToArray();
                }
            }
        }

        config.AdminRoles = [];
        config.EnableLiveTv = false;
        config.EnableLiveTvManagement = false;
        config.EnableLiveTvRoles = false;
        config.LiveTvRoles = [];
        config.LiveTvManagementRoles = [];
        config.EnableFolderRoles = false;
        config.FolderRoleMapping = [];
    }

    public static ResolvedPermissions Resolve(ProviderConfig config, string[] roles, Guid? userId)
    {
        IdentityPolicy.Admit(config, roles);
        var values = new Dictionary<PermissionKind, bool>();
        var sources = new Dictionary<PermissionKind, string>();
        foreach (var entry in config.PermissionDefaults ?? new())
        {
            var kind = Enum.Parse<PermissionKind>(entry.Key);
            values[kind] = entry.Value;
            sources[kind] = "Everyone";
        }

        string GroupSource(IEnumerable<string> groups) => "Group: " + string.Join(", ", groups);
        var matching = config.GroupPermissions.Where(r => roles.Contains(r.Role, StringComparer.Ordinal)).ToArray();
        foreach (var grant in matching.SelectMany(r => r.Permissions.Where(p => p.Value).Select(p => (r.Role, p.Key))).GroupBy(p => p.Key))
        {
            var kind = Enum.Parse<PermissionKind>(grant.Key);
            values[kind] = true;
            sources[kind] = GroupSource(grant.Select(p => p.Role));
        }

        var allFolders = config.EnableAllFolders;
        var folders = allFolders ? [] : config.EnabledFolders.Distinct(StringComparer.Ordinal).ToArray();
        var folderSource = "Everyone";
        var allGroups = matching.Where(r => r.LibraryMode == "All").Select(r => r.Role).ToArray();
        var selectedGroups = matching.Where(r => r.LibraryMode == "Selected").ToArray();
        if (!allFolders && allGroups.Length > 0)
        {
            allFolders = true;
            folders = [];
            folderSource = GroupSource(allGroups);
        }
        else if (!allFolders && selectedGroups.Length > 0)
        {
            folders = folders.Concat(selectedGroups.SelectMany(r => r.Folders)).Distinct(StringComparer.Ordinal).ToArray();
            folderSource = (config.EnabledFolders.Length > 0 ? "Everyone, " : string.Empty) + GroupSource(selectedGroups.Select(r => r.Role));
        }

        var user = config.UserPermissions.SingleOrDefault(r => r.UserId == userId);
        if (user is not null)
        {
            foreach (var entry in user.Permissions)
            {
                var kind = Enum.Parse<PermissionKind>(entry.Key);
                values[kind] = entry.Value;
                sources[kind] = "User override";
            }

            if (user.LibraryMode != "Inherit")
            {
                allFolders = user.LibraryMode == "All";
                folders = allFolders ? [] : user.Folders.Distinct(StringComparer.Ordinal).ToArray();
                folderSource = "User override";
            }
        }

        values[PermissionKind.EnableAllFolders] = allFolders;
        sources[PermissionKind.EnableAllFolders] = folderSource;
        return new(config.EnableAuthorization && user?.PreservePermissions != true, values, sources, folders, folderSource);
    }

    public static PermissionPreviewRow[] Describe(ResolvedPermissions? resolved, User? user, string? blockedReason = null)
    {
        return Catalogue.Select(definition =>
        {
            var kind = Enum.Parse<PermissionKind>(definition.Key);
            bool? current = user?.HasPermission(kind);
            var managed = resolved?.Synchronize == true && resolved.Values.ContainsKey(kind) && blockedReason is null;
            bool? effective = managed ? resolved!.Values[kind] : current;
            var source = managed ? resolved!.Sources[kind]
                : blockedReason ?? (kind == PermissionKind.IsDisabled ? "Managed by Jellyfin"
                : resolved?.Synchronize == false ? "Permission synchronization is off" : "Keep Jellyfin setting");
            var change = !managed ? "Unmanaged" : current == effective ? "Unchanged" : effective == true ? "On" : "Off";
            return new PermissionPreviewRow(definition.Key, definition.Name, definition.Category, current, effective, managed, change, source);
        }).ToArray();
    }
}

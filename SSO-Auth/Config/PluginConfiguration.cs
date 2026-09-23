#nullable enable
using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SSO_Auth.Config;

public class PluginConfiguration : BasePluginConfiguration
{
    public int SchemaVersion { get; set; }

    [XmlElement("SamlConfigs")]
    public SerializableDictionary<string, SamlConfig> SamlConfigs { get; set; } = new();

    [XmlElement("OidConfigs")]
    public SerializableDictionary<string, OidConfig> OidConfigs { get; set; } = new();
}

// Shared fields retain their legacy XML names and meanings.
public class ProviderConfig
{
    public bool Enabled { get; set; }

    public bool EnableAuthorization { get; set; }

    // Library access for everyone; group rules and user overrides add to or replace it.
    public bool EnableAllFolders { get; set; }

    public string[] EnabledFolders { get; set; } = [];

    public string[] Roles { get; set; } = [];

    // Release-era settings, accepted from saved XML and the provider API. Normalization
    // converts them into PermissionDefaults and GroupPermissions, then clears them.
    public string[] AdminRoles { get; set; } = [];

    public bool EnableFolderRoles { get; set; }

    public bool EnableLiveTvRoles { get; set; }

    public bool EnableLiveTv { get; set; }

    public bool EnableLiveTvManagement { get; set; }

    public string[] LiveTvRoles { get; set; } = [];

    public string[] LiveTvManagementRoles { get; set; } = [];

    [XmlArray("FolderRoleMappings")]
    [XmlArrayItem(typeof(FolderRoleMap), ElementName = "FolderRoleMappings")]
    public List<FolderRoleMap> FolderRoleMapping { get; set; } = [];

    // Null only for release-era configurations, which predate permission rules.
    public SerializableDictionary<string, bool>? PermissionDefaults { get; set; }

    public List<GroupPermissionRule> GroupPermissions { get; set; } = [];

    public List<UserPermissionRule> UserPermissions { get; set; } = [];

    public SerializableDictionary<string, UserRoleSnapshot> UserRoleSnapshots { get; set; } = new();

    public string DefaultProvider { get; set; } = string.Empty;

    public string SchemeOverride { get; set; } = string.Empty;

    public int? PortOverride { get; set; }

    public bool NewPath { get; set; }

    // Saved username mappings remain usable for upstream compatibility.
    [XmlElement("CanonicalLinks")]
    public SerializableDictionary<string, Guid> CanonicalLinks { get; set; } = new();

    [XmlElement("SubjectLinks")]
    public SerializableDictionary<string, Guid> SubjectLinks { get; set; } = new();

    // Display details for SubjectLinks, keyed the same way; shown on the account connections page.
    [XmlElement("SubjectLinkDetails")]
    public SerializableDictionary<string, SubjectLinkDetail> SubjectLinkDetails { get; set; } = new();
}

[XmlRoot("PluginConfiguration")]
public class SamlConfig : ProviderConfig
{
    public string SamlEndpoint { get; set; } = string.Empty;

    public string SamlClientId { get; set; } = string.Empty;

    public string SamlCertificate { get; set; } = string.Empty;

    public string SamlIssuer { get; set; } = string.Empty;

    public string SamlNameIdFormat { get; set; } = "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent";
}

[XmlRoot("PluginConfiguration")]
public class OidConfig : ProviderConfig
{
    public string OidEndpoint { get; set; } = string.Empty;

    public string OidClientId { get; set; } = string.Empty;

    public string OidSecret { get; set; } = string.Empty;

    public string RoleClaim { get; set; } = string.Empty;

    public bool UseZitadelRoles { get; set; }

    public string[] ZitadelOrganizationIds { get; set; } = [];

    public string[] OidScopes { get; set; } = [];

    public string DefaultUsernameClaim { get; set; } = string.Empty;

    public string AvatarUrlFormat { get; set; } = string.Empty;

    public bool DisableHttps { get; set; }

    public bool DisablePushedAuthorization { get; set; }

    public bool DoNotValidateEndpoints { get; set; }

    public bool DoNotValidateIssuerName { get; set; }

    public bool DoNotLoadProfile { get; set; }
}

public class FolderRoleMap
{
    public string Role { get; set; } = string.Empty;

    public List<string> Folders { get; set; } = [];
}

public class PermissionRule
{
    public SerializableDictionary<string, bool> Permissions { get; set; } = new();

    public string LibraryMode { get; set; } = "Inherit";

    public string[] Folders { get; set; } = [];
}

// Groups only grant: every permission value is true, and LibraryMode "Inherit" adds no libraries.
public class GroupPermissionRule : PermissionRule
{
    public string Role { get; set; } = string.Empty;
}

public class UserPermissionRule : PermissionRule
{
    public Guid UserId { get; set; }

    public bool PreservePermissions { get; set; }
}

public class SubjectLinkDetail
{
    public string Username { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;

    public DateTime? LinkedAt { get; set; }

    public DateTime? LastSignInAt { get; set; }
}

public class UserRoleSnapshot
{
    public string[] Roles { get; set; } = [];

    public DateTimeOffset ObservedAt { get; set; }
}

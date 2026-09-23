#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SSO_Auth.Config;

namespace Jellyfin.Plugin.SSO_Auth.Services;

internal static class ProviderValidation
{
    // Only administrator settings writes use this boundary. Startup migration and
    // account/link persistence must remain possible with incomplete legacy providers.
    public static void ValidateChanges(PluginConfiguration updated, PluginConfiguration previous)
    {
        ValidateChanges(updated.OidConfigs, previous.OidConfigs);
        ValidateChanges(updated.SamlConfigs, previous.SamlConfigs);
    }

    private static void ValidateChanges<T>(IDictionary<string, T> updated, IDictionary<string, T> previous)
        where T : ProviderConfig
    {
        foreach (var (name, config) in updated)
        {
            previous.TryGetValue(name, out var saved);
            if (saved is not null && ConfigurationMigration.Fingerprint(config) == ConfigurationMigration.Fingerprint(saved))
            {
                continue;
            }

            // Preserve old UriBuilder sentinel meanings until the port itself changes.
            var legacyPort = config.PortOverride is 0 or -1 && saved?.PortOverride == config.PortOverride;
            if (config.PortOverride is < 1 or > 65535 && !legacyPort)
            {
                throw new SsoException("PortOverride must be blank or an integer from 1 to 65535.");
            }

            if (!config.Enabled)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(config.SchemeOverride) && config.SchemeOverride is not ("http" or "https"))
            {
                throw new SsoException("SchemeOverride must be blank, http, or https.");
            }

            if (config is OidConfig oid)
            {
                OidcAuthority(oid);
                RoleMapping(oid);
            }
            else if (config is SamlConfig saml)
            {
                SamlEndpoint(saml);
                using var certificate = SamlAdapter.SigningCertificate(saml);
            }
        }
    }

    public static Uri OidcAuthority(OidConfig config)
    {
        if (!Uri.TryCreate(config.OidEndpoint?.Trim(), UriKind.Absolute, out var authority)
            || (authority.Scheme != "https" && !(config.DisableHttps && authority.Scheme == "http")))
        {
            throw new SsoException("OidEndpoint must be a valid HTTPS authority URL (HTTP requires DisableHttps).");
        }

        if (string.IsNullOrWhiteSpace(config.OidClientId))
        {
            throw new SsoException("OidClientId is required for an enabled OIDC provider.");
        }

        return authority;
    }

    public static Uri SamlEndpoint(SamlConfig config)
    {
        if (!Uri.TryCreate(config.SamlEndpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https")
        {
            throw new SsoException("SamlEndpoint must be a valid HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(config.SamlIssuer))
        {
            throw new SsoException("SamlIssuer is required for an enabled SAML provider.");
        }

        if (string.IsNullOrWhiteSpace(config.SamlClientId))
        {
            throw new SsoException("SamlClientId is required for an enabled SAML provider.");
        }

        if (string.IsNullOrWhiteSpace(config.SamlNameIdFormat) || config.SamlNameIdFormat.EndsWith(":transient", StringComparison.Ordinal))
        {
            throw new SsoException("SamlNameIdFormat must specify a stable, non-transient NameID format. Legacy settings and links have been preserved.");
        }

        return endpoint;
    }

    public static void RoleMapping(OidConfig config)
    {
        if (config.UseZitadelRoles && (string.IsNullOrWhiteSpace(config.RoleClaim)
            || config.ZitadelOrganizationIds is not { Length: > 0 }
            || config.ZitadelOrganizationIds.Any(string.IsNullOrWhiteSpace)))
        {
            throw new SsoException("ZITADEL role mapping requires RoleClaim and at least one nonblank ZitadelOrganizationIds entry.");
        }
    }
}

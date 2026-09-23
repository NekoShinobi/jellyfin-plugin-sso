using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SSO_Auth;

/// <summary>
/// The SSO plugin class.
/// </summary>
public class SSOPlugin : BasePlugin<PluginConfiguration>, IPlugin, IHasWebPages
{
    private readonly object _configurationGate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SSOPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Internal Jellyfin interface for the ApplicationPath.</param>
    /// <param name="xmlSerializer">Internal Jellyfin interface for the XML information.</param>
    public SSOPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        // Bypass the host's fallback that overwrites an unreadable configuration.
        var exists = File.Exists(ConfigurationFilePath);
        var config = exists
            ? (PluginConfiguration)xmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), ConfigurationFilePath)
            : new PluginConfiguration();
        if (exists && config.SchemaVersion == 0 && !File.Exists(ConfigurationFilePath + ".pre-v1.bak"))
        {
            File.Copy(ConfigurationFilePath, ConfigurationFilePath + ".pre-v1.bak");
        }

        Configuration = ConfigurationMigration.Normalize(config);
        SaveConfiguration();
        Instance = this;
    }

    /// <summary>
    /// Gets the instance of the SSO plugin.
    /// </summary>
    public static SSOPlugin Instance { get; private set; }

    internal object ConfigurationGate => _configurationGate;

    /// <summary>
    /// Gets the name of the SSO plugin.
    /// </summary>
    public override string Name => "SSO-Auth";

    /// <summary>
    /// Gets the GUID of the SSO plugin.
    /// </summary>
    public override Guid Id => Guid.Parse("505ce9d1-d916-42fa-86ca-673ef241d7df");

    /// <inheritdoc />
    public override void SaveConfiguration(PluginConfiguration config)
    {
        lock (_configurationGate)
        {
            ConfigurationMigration.Normalize(config);
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationFilePath)!);
            var temporary = ConfigurationFilePath + ".tmp";
            XmlSerializer.SerializeToFile(config, temporary);
            File.Move(temporary, ConfigurationFilePath, true);
        }
    }

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        lock (_configurationGate)
        {
            var normalized = ConfigurationMigration.Normalize((PluginConfiguration)configuration);
            // Dashboard snapshots may predate a completed link. Link mutations use PersistConfiguration.
            foreach (var entry in normalized.OidConfigs)
            {
                if (Configuration.OidConfigs.TryGetValue(entry.Key, out var current))
                {
                    entry.Value.SubjectLinks = current.SubjectLinks;
                    entry.Value.CanonicalLinks = current.CanonicalLinks;
                    entry.Value.UserRoleSnapshots = current.UserRoleSnapshots;
                }
            }

            foreach (var entry in normalized.SamlConfigs)
            {
                if (Configuration.SamlConfigs.TryGetValue(entry.Key, out var current))
                {
                    entry.Value.SubjectLinks = current.SubjectLinks;
                    entry.Value.CanonicalLinks = current.CanonicalLinks;
                    entry.Value.UserRoleSnapshots = current.UserRoleSnapshots;
                }
            }

            PersistConfiguration(normalized);
        }
    }

    public void PersistConfiguration(PluginConfiguration configuration)
    {
        lock (_configurationGate)
        {
            SaveConfiguration(configuration);
            base.UpdateConfiguration(configuration);
        }
    }

    /// <summary>
    /// Returns the available internal web pages of this plugin.
    /// </summary>
    /// <returns>A list of internal webpages in this application.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "SSO",
                EnableInMainMenu = true,
                MenuSection = "plugins",
                MenuIcon = "vpn_key",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.configPage.html"
            },
            new PluginPageInfo
            {
                Name = Name + ".js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.config.js"
            },
            new PluginPageInfo
            {
                Name = Name + ".permissions.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.permissions.js"
            },
            new PluginPageInfo
            {
                Name = Name + ".css",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.style.css"
            },
            new PluginPageInfo
            {
                Name = Name + "-linking",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.linking.html"
            },
            new PluginPageInfo
            {
                Name = Name + "-linking.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.linking.js"
            },
        };
    }

    /// <summary>
    /// Returns the available user views for this plugin.
    /// </summary>
    /// <returns>A list of user views for this plugin.</returns>
    public IEnumerable<PluginPageInfo> GetViews()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "style.css",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.style.css"
            },
            new PluginPageInfo
            {
                Name = "linking",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.linking.html"
            },
            new PluginPageInfo
            {
                Name = "linking.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.linking.js"
            },
            new PluginPageInfo
            {
                Name = "web.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Views.web.js"
            },
            new PluginPageInfo
            {
                Name = "complete.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Views.complete.js"
            },
        };
    }
}

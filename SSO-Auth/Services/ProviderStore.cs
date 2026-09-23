#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.SSO_Auth.Config;

namespace Jellyfin.Plugin.SSO_Auth.Services;

public static class ConfigurationMigration
{
    public const int CurrentVersion = 1;

    public static PluginConfiguration Normalize(PluginConfiguration configuration)
    {
        if (configuration.SchemaVersion > CurrentVersion)
        {
            throw new SsoException("Configuration was written by a newer plugin. Restore a matching backup.");
        }

        configuration.OidConfigs ??= new();
        configuration.SamlConfigs ??= new();
        foreach (var key in configuration.OidConfigs.Keys.ToArray())
        {
            configuration.OidConfigs[key] ??= new();
            NormalizeProvider(configuration.OidConfigs[key]);
            configuration.OidConfigs[key].OidScopes ??= [];
            configuration.OidConfigs[key].ZitadelOrganizationIds ??= [];
        }

        foreach (var key in configuration.SamlConfigs.Keys.ToArray())
        {
            configuration.SamlConfigs[key] ??= new();
            NormalizeProvider(configuration.SamlConfigs[key]);
        }

        configuration.SchemaVersion = CurrentVersion;
        return configuration;
    }

    public static void NormalizeProvider(ProviderConfig config)
    {
        config.DefaultProvider = config.DefaultProvider?.Trim() ?? string.Empty;
        config.Roles ??= [];
        config.AdminRoles ??= [];
        config.EnabledFolders ??= [];
        config.LiveTvRoles ??= [];
        config.LiveTvManagementRoles ??= [];
        config.FolderRoleMapping ??= [];
        config.CanonicalLinks ??= new();
        config.SubjectLinks ??= new();
        config.SubjectLinkDetails ??= new();
        foreach (var key in config.SubjectLinkDetails.Keys.Where(k => !config.SubjectLinks.ContainsKey(k)).ToArray())
        {
            config.SubjectLinkDetails.Remove(key);
        }

        foreach (var mapping in config.FolderRoleMapping)
        {
            if (mapping is null)
            {
                throw new SsoException("A folder mapping is null. Repair the saved configuration before continuing.");
            }

            mapping.Folders ??= [];
        }

        PermissionPolicy.Normalize(config);
    }

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public static string Fingerprint(ProviderConfig config)
    {
        var json = JsonSerializer.SerializeToNode(config, config.GetType())!.AsObject();
        json.Remove(nameof(ProviderConfig.CanonicalLinks));
        json.Remove(nameof(ProviderConfig.SubjectLinks));
        json.Remove(nameof(ProviderConfig.SubjectLinkDetails));
        json.Remove(nameof(ProviderConfig.UserRoleSnapshots));
        return LoginTransactions.Hash(json.ToJsonString());
    }
}

public sealed class ProviderStore(Func<PluginConfiguration> read, Action<PluginConfiguration> write, object? synchronization = null)
{
    private readonly object _gate = synchronization ?? new();

    public PluginConfiguration Snapshot()
    {
        lock (_gate)
        {
            return ConfigurationMigration.Normalize(ConfigurationMigration.Clone(read()));
        }
    }

    public ProviderConfig Get(string mode, string name, bool requireEnabled = true)
    {
        var config = Snapshot();
        ProviderConfig? provider = mode == "OID" ? config.OidConfigs.GetValueOrDefault(name) : config.SamlConfigs.GetValueOrDefault(name);
        if (provider is null || (requireEnabled && !provider.Enabled))
        {
            throw new SsoException("Provider not found or disabled.", 404);
        }

        return provider;
    }

    public void Edit(Action<PluginConfiguration> update)
    {
        lock (_gate)
        {
            var config = Snapshot();
            update(config);
            write(ConfigurationMigration.Normalize(config));
        }
    }

    internal void EditSettings(Action<PluginConfiguration> update) => Edit(config =>
    {
        var previous = ConfigurationMigration.Clone(config);
        update(config);
        ConfigurationMigration.Normalize(config);
        ProviderValidation.ValidateChanges(config, previous);
    });

    public static ProviderConfig Find(PluginConfiguration config, string mode, string name) => mode == "OID" ? config.OidConfigs[name] : config.SamlConfigs[name];

    public void EnsureUnchanged(LoginTransaction transaction) => GetUnchanged(transaction);

    internal ProviderConfig GetUnchanged(LoginTransaction transaction)
    {
        var config = Get(transaction.Mode, transaction.Provider);
        if (ConfigurationMigration.Fingerprint(config) != transaction.SettingsHash)
        {
            throw new SsoException("Provider settings changed during sign-in. Start again.");
        }

        return config;
    }
}

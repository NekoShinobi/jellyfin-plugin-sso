#nullable enable
using System;
using Jellyfin.Plugin.SSO_Auth.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SSO_Auth;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<LoginTransactions>();
        serviceCollection.AddSingleton(_ => new ProviderStore(() => SSOPlugin.Instance.Configuration, c => SSOPlugin.Instance.PersistConfiguration(c), SSOPlugin.Instance.ConfigurationGate));
        serviceCollection.AddSingleton<OidcAdapter>();
        serviceCollection.AddSingleton<SamlAdapter>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.Authentication.IAuthenticationProvider, SsoAuthenticationProvider>();
        serviceCollection.AddSingleton<AccountService>();
        serviceCollection.AddHostedService<LegacyProviderMigration>();
        serviceCollection.AddHostedService<WebMenuIntegration>();
        serviceCollection.AddSingleton<IAvatarService, AvatarService>();
        serviceCollection.AddHttpClient("sso-avatar").RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(AvatarService.CreateHandler);
        serviceCollection.AddHttpClient("sso-protocol", client => client.Timeout = TimeSpan.FromSeconds(30));
    }
}

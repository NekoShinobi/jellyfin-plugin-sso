#nullable enable
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SSO_Auth.Services;

public sealed class LegacyProviderMigration(IUserManager users, ILogger<LegacyProviderMigration> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var repaired = 0;
        foreach (var user in users.GetUsers().Where(u => u.AuthenticationProviderId == AccountService.LegacyProvider))
        {
            cancellationToken.ThrowIfCancellationRequested();
            user.AuthenticationProviderId = AccountService.ProviderId;
            await users.UpdateUserAsync(user).ConfigureAwait(false);
            repaired++;
        }

        logger.LogInformation("SSO migration schema {Schema}; repaired {Count} legacy authentication-provider references. Existing user IDs and configuration backup retained.", ConfigurationMigration.CurrentVersion, repaired);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

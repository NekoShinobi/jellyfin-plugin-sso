#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Duende.IdentityModel.OidcClient;
using Jellyfin.Plugin.SSO_Auth.Config;

namespace Jellyfin.Plugin.SSO_Auth.Services;

public sealed record LoginTransaction(string Mode, string Provider, string Callback, string BrowserHash, Guid? TargetUser, string SettingsHash, ProviderConfig Settings, AuthorizeState? OidState = null, string? SamlRequestId = null, string? Nonce = null);

public sealed record LoginCompletion(LoginTransaction Transaction, ExternalIdentity Identity);

// All transitions are atomic; protocol callbacks and final completions use separate namespaces.
public sealed class LoginTransactions : IDisposable
{
    private const int Capacity = 4096;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly ITimer _timer;

    public LoginTransactions(TimeProvider clock)
    {
        _clock = clock;
        _timer = clock.CreateTimer(_ => Prune(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                Prune();
                return _entries.Count;
            }
        }
    }

    public static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public void Add<T>(string key, T value, TimeSpan lifetime)
        where T : class
    {
        lock (_gate)
        {
            Prune();
            if (_entries.Count >= Capacity || !_entries.TryAdd(key, new Entry(value, _clock.GetUtcNow() + lifetime)))
            {
                throw new SsoException("Too many sign-in attempts. Try again later.", 429);
            }
        }
    }

    public T Take<T>(string key, Func<T, bool> matches)
        where T : class
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Expires <= _clock.GetUtcNow())
            {
                _entries.Remove(key);
                throw new SsoException("Sign-in expired or already used. Start again.");
            }

            if (entry.Value is not T value || !matches(value))
            {
                throw new SsoException("Sign-in does not match this browser, provider, or operation.", 403);
            }

            _entries.Remove(key);
            return value;
        }
    }

    public bool RememberReplay(string key, DateTimeOffset expires)
    {
        lock (_gate)
        {
            Prune();
            return expires > _clock.GetUtcNow() && _entries.Count < Capacity && _entries.TryAdd("replay:" + key, new Entry(key, expires));
        }
    }

    private void Prune()
    {
        lock (_gate)
        {
            foreach (var key in _entries.Where(e => e.Value.Expires <= _clock.GetUtcNow()).Select(e => e.Key).ToArray())
            {
                _entries.Remove(key);
            }
        }
    }

    public void Dispose() => _timer.Dispose();

    private sealed record Entry(object Value, DateTimeOffset Expires);
}

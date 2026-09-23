#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SSO_Auth.Config;

namespace Jellyfin.Plugin.SSO_Auth.Services;

public sealed class SsoException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record ExternalIdentity(string Issuer, string Subject, string DisplayName, string[] Roles, string? AvatarUrl = null)
{
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { Issuer, Subject }))));
}

public static class IdentityPolicy
{
    public static string[] ReadRoles(IEnumerable<Claim> claims, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var segments = Regex.Split(path.Trim(), @"(?<!\\)\.").Select(s => s.Replace(@"\.", ".", StringComparison.Ordinal)).ToArray();
        var values = new List<string>();
        foreach (var claim in claims.Where(c => c.Type == segments[0]))
        {
            var value = claim.Value.Trim();
            if (segments.Length == 1 && !value.StartsWith('[') && !value.StartsWith('{'))
            {
                values.Add(value);
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(value);
                var node = document.RootElement;
                foreach (var segment in segments.Skip(1))
                {
                    if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out node))
                    {
                        throw new SsoException("The configured role claim is missing or malformed.");
                    }
                }

                if (node.ValueKind == JsonValueKind.String)
                {
                    values.Add(node.GetString()!);
                }
                else if (node.ValueKind == JsonValueKind.Array && node.EnumerateArray().All(n => n.ValueKind == JsonValueKind.String))
                {
                    values.AddRange(node.EnumerateArray().Select(n => n.GetString()!));
                }
                else
                {
                    throw new SsoException("Role claims must contain strings.");
                }
            }
            catch (JsonException)
            {
                throw new SsoException("The configured role claim is malformed.");
            }
        }

        return values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static void Admit(ProviderConfig config, IReadOnlyCollection<string> roles)
    {
        if (config.Roles.Length > 0 && !config.Roles.Any(roles.Contains))
        {
            throw new SsoException("This account is not admitted by the provider's login policy.", 403);
        }
    }
}

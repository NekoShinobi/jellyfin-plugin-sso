#nullable enable
using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Results;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.SSO_Auth.Services;

// Duende handles discovery, code exchange, PKCE, state, UserInfo and at_hash.
// Microsoft's maintained token handler performs JWT parsing and cryptographic validation.
public sealed class OidcTokenValidator(string? nonce) : IIdentityTokenValidator
{
    public async Task<IdentityTokenValidationResult> ValidateAsync(string identityToken, OidcClientOptions options, CancellationToken cancellationToken = default)
    {
        var handler = new JsonWebTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 128 * 1024 };
        var keys = new JsonWebKeySet(JsonSerializer.Serialize(options.ProviderInformation.KeySet)).GetSigningKeys();
        var validation = await handler.ValidateTokenAsync(identityToken, new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidateIssuer = true,
            ValidIssuer = options.ProviderInformation.IssuerName,
            ValidateAudience = true,
            ValidAudience = options.ClientId,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512],
        }).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return new IdentityTokenValidationResult { Error = validation.Exception is SecurityTokenSignatureKeyNotFoundException ? "invalid_signature" : "invalid_identity_token" };
        }

        var token = (JsonWebToken)validation.SecurityToken;
        var claims = validation.ClaimsIdentity;
        var authorizedParty = claims.FindFirst("azp")?.Value;
        if (string.IsNullOrEmpty(nonce) || claims.FindFirst("nonce")?.Value != nonce
            || string.IsNullOrWhiteSpace(claims.FindFirst("sub")?.Value)
            || claims.FindFirst("iat") is null
            || (authorizedParty is not null && authorizedParty != options.ClientId)
            || (token.Audiences.Count() > 1 && authorizedParty != options.ClientId))
        {
            return new IdentityTokenValidationResult { Error = "invalid_identity_binding" };
        }

        return new IdentityTokenValidationResult { User = new ClaimsPrincipal(claims), SignatureAlgorithm = token.Alg };
    }
}

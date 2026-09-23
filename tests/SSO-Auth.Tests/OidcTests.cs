using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Services;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace SSO_Auth.Tests;

public class OidcTests
{
    private sealed class Provider : HttpMessageHandler, IHttpClientFactory
    {
        public readonly RSA Key = RSA.Create(2048);
        public string Issuer = "https://idp.example/realm";
        public string Nonce = "";
        public string Invalid = "";
        public object Roles = new[] { "allowed", "admin" };
        public string RoleClaim = "groups";
        public Dictionary<string, object> ExtraClaims = new();
        public string? Challenge;
        public bool ProfileRequested;
        public bool ParRequested;
        public bool MultiHost;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        private static string B64(byte[] value) => WebEncoders.Base64UrlEncode(value);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object body;
            if (request.RequestUri!.AbsolutePath.EndsWith("openid-configuration"))
            {
                body = new
                {
                    issuer = Issuer,
                    authorization_endpoint = Issuer + "/authorize",
                    token_endpoint = (MultiHost ? "https://tokens.example" : Issuer) + "/token",
                    jwks_uri = Issuer + "/jwks",
                    userinfo_endpoint = Issuer + "/userinfo",
                    pushed_authorization_request_endpoint = Issuer + "/par",
                    response_types_supported = new[] { "code" },
                    subject_types_supported = new[] { "public" },
                    id_token_signing_alg_values_supported = new[] { "RS256" },
                    token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post" }
                };
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("jwks"))
            {
                var key = Key.ExportParameters(false);
                body = new { keys = new[] { new { kty = "RSA", use = "sig", kid = "test", alg = "RS256", n = B64(key.Modulus!), e = B64(key.Exponent!) } } };
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("par"))
            {
                ParRequested = true;
                var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                Nonce = form["nonce"].ToString();
                Challenge = form["code_challenge"].ToString();
                body = new { request_uri = "urn:ietf:params:oauth:request_uri:fixture", expires_in = 90 };
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("token"))
            {
                var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal(Challenge, B64(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))));
                Assert.Equal("https://jellyfin.example/base/sso/OID/redirect/test", form["redirect_uri"].ToString());
                var claims = new Dictionary<string, object>
                {
                    ["iss"] = Invalid == "issuer" ? "https://attacker.example" : Issuer,
                    ["sub"] = "stable-subject",
                    ["aud"] = Invalid == "audience" ? "other" : "jellyfin",
                    ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["exp"] = DateTimeOffset.UtcNow.AddMinutes(Invalid == "expiry" ? -10 : 5).ToUnixTimeSeconds(),
                    ["nonce"] = Invalid == "nonce" ? "other" : Nonce,
                    ["preferred_username"] = "display-name",
                    [RoleClaim] = Roles,
                };
                foreach (var claim in ExtraClaims) claims[claim.Key] = claim.Value;
                var jwt = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "test", typ = "JWT" })) + "." + B64(JsonSerializer.SerializeToUtf8Bytes(claims));
                var signature = Key.SignData(Encoding.ASCII.GetBytes(jwt), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                if (Invalid == "signature") signature[0] ^= 1;
                body = new { id_token = jwt + "." + B64(signature), access_token = "synthetic-access", token_type = "Bearer", expires_in = 300 };
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("userinfo"))
            {
                ProfileRequested = true;
                body = new { sub = Invalid == "userinfo-subject" ? "attacker" : "stable-subject", preferred_username = "profile-name" };
            }
            else throw new InvalidOperationException("Unexpected fixture endpoint");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing) { if (disposing) Key.Dispose(); base.Dispose(disposing); }
    }

    [Theory]
    [InlineData("authentik", "groups", false, false, false)]
    [InlineData("authelia", "groups", false, false, false)]
    [InlineData("keycloak", "realm_access.roles", false, false, false)]
    [InlineData("pocket-id", "groups", true, false, false)]
    [InlineData("kanidm-par", "groups", true, true, false)]
    [InlineData("google-multi-host", "groups", false, false, true)]
    public async Task ProviderShapesPreservePkceNonceRolesAndOptionalUserInfo(string fixture, string roleClaim, bool noProfile, bool par, bool multiHost)
    {
        using var provider = new Provider { MultiHost = multiHost };
        var config = new OidConfig { OidEndpoint = provider.Issuer, OidClientId = "jellyfin", OidSecret = "synthetic", RoleClaim = roleClaim, DoNotLoadProfile = noProfile, DisablePushedAuthorization = !par, DoNotValidateEndpoints = multiHost };
        if (roleClaim.Contains('.')) { provider.RoleClaim = "realm_access"; provider.Roles = new { roles = new[] { "allowed", "admin" } }; }
        var (adapter, transaction) = await Start(provider, config);
        var identity = await adapter.Verify(transaction, "?code=synthetic&state=" + transaction.OidState!.State);
        Assert.Equal(provider.Issuer, identity.Issuer);
        Assert.Equal("stable-subject", identity.Subject);
        Assert.Contains("allowed", identity.Roles);
        Assert.Equal(!noProfile, provider.ProfileRequested);
        Assert.Equal(par, provider.ParRequested);
        Assert.NotEmpty(fixture);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("nonce")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expiry")]
    [InlineData("userinfo-subject")]
    [InlineData("state")]
    public async Task RejectsUnverifiedIdentity(string invalid)
    {
        using var provider = new Provider { Invalid = invalid };
        var config = new OidConfig { OidEndpoint = provider.Issuer, OidClientId = "jellyfin", DisablePushedAuthorization = true };
        var (adapter, transaction) = await Start(provider, config);
        await Assert.ThrowsAsync<SsoException>(() => adapter.Verify(transaction, "?code=synthetic&state=" + (invalid == "state" ? "other" : transaction.OidState!.State)));
    }

    [Theory]
    [InlineData("@{avatar-url}", "https://images.example/a b.png?size=1&name=a/b")]
    [InlineData("@{picture}", "https://images.example/a b.png?size=1&name=a/b")]
    [InlineData("https://images.example/@{filename}?name=@{name}", "https://images.example/a%2Fb%3F.png?name=a%26b%3Dc")]
    [InlineData("@{missing}", null)]
    [InlineData("@{avatar-url}/@{filename}", "https%3A%2F%2Fimages.example%2Fa%20b.png%3Fsize%3D1%26name%3Da%2Fb/a%2Fb%3F.png")]
    public async Task AvatarTemplatesDistinguishWholeUrlsFromComponents(string format, string? expected)
    {
        using var provider = new Provider { ExtraClaims = new()
        {
            ["avatar-url"] = "https://images.example/a b.png?size=1&name=a/b",
            ["picture"] = "https://images.example/a b.png?size=1&name=a/b",
            ["filename"] = "a/b?.png", ["name"] = "a&b=c",
        } };
        var config = new OidConfig { OidEndpoint = provider.Issuer, OidClientId = "jellyfin", AvatarUrlFormat = format, DisablePushedAuthorization = true };
        var (adapter, transaction) = await Start(provider, config);
        var identity = await adapter.Verify(transaction, "?code=synthetic&state=" + transaction.OidState!.State);
        Assert.Equal(expected, identity.AvatarUrl);
    }

    [Fact]
    public async Task SignedZitadelRolesAreOptInScopedAndRevocable()
    {
        using var provider = new Provider { RoleClaim = "urn:zitadel:iam:org:project:roles", Roles = new Dictionary<string, object>
        {
            ["allowed"] = new Dictionary<string, string> { ["org-a"] = "example.test", ["org-b"] = "other.test" },
            ["admin"] = new Dictionary<string, string> { ["org-b"] = "other.test" },
            ["download"] = new Dictionary<string, string> { ["org-a"] = "example.test" },
        } };
        var config = new OidConfig { OidEndpoint = provider.Issuer, OidClientId = "jellyfin", RoleClaim = provider.RoleClaim,
            DisablePushedAuthorization = true, UseZitadelRoles = true, ZitadelOrganizationIds = ["org-a"], Roles = ["allowed"],
            EnableAuthorization = true, AdminRoles = ["admin"], GroupPermissions = [new() { Role = "download", Permissions = new() { ["EnableContentDownloading"] = true } }] };
        ConfigurationMigration.NormalizeProvider(config);
        async Task<ExternalIdentity> Verify()
        {
            var (adapter, transaction) = await Start(provider, config);
            return await adapter.Verify(transaction, "?code=synthetic&state=" + transaction.OidState!.State);
        }
        var identity = await Verify();
        Assert.Equal(new[] { "allowed", "download" }, identity.Roles);
        var policy = PermissionPolicy.Resolve(config, identity.Roles, null);
        Assert.False(policy.Values[Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator]);
        Assert.True(policy.Values[Jellyfin.Database.Implementations.Enums.PermissionKind.EnableContentDownloading]);
        config.ZitadelOrganizationIds = ["org-a", "org-b"];
        identity = await Verify();
        Assert.True(PermissionPolicy.Resolve(config, identity.Roles, null).Values[Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator]);
        config.ZitadelOrganizationIds = ["org-a"];
        provider.Roles = new Dictionary<string, object> { ["allowed"] = new Dictionary<string, string> { ["org-a"] = "example.test" } };
        identity = await Verify();
        policy = PermissionPolicy.Resolve(config, identity.Roles, null);
        Assert.False(policy.Values[Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator]);
        Assert.False(policy.Values[Jellyfin.Database.Implementations.Enums.PermissionKind.EnableContentDownloading]);
        provider.Roles = new Dictionary<string, object> { ["allowed"] = new Dictionary<string, string> { ["org-b"] = "other.test" } };
        identity = await Verify();
        Assert.Empty(identity.Roles);
        Assert.Equal(403, Assert.Throws<SsoException>(() => IdentityPolicy.Admit(config, identity.Roles)).Status);
        config.UseZitadelRoles = false;
        await Assert.ThrowsAsync<SsoException>(Verify);
    }

    private static async Task<(OidcAdapter, LoginTransaction)> Start(Provider provider, OidConfig config)
    {
        const string callback = "https://jellyfin.example/base/sso/OID/redirect/test";
        var adapter = new OidcAdapter(provider);
        var nonce = LoginTransactions.Secret();
        var state = await adapter.Client(config, callback, nonce).PrepareLoginAsync(new Duende.IdentityModel.Client.Parameters { { "nonce", nonce } });
        Assert.False(state.IsError, state.Error);
        if (!provider.ParRequested)
        {
            var query = QueryHelpers.ParseQuery(new Uri(state.StartUrl).Query);
            provider.Nonce = query["nonce"].ToString();
            provider.Challenge = query["code_challenge"].ToString();
            Assert.Equal("S256", query["code_challenge_method"].ToString());
        }
        return (adapter, new LoginTransaction("OID", "test", callback, "browser", null, ConfigurationMigration.Fingerprint(config), config, OidState: state, Nonce: nonce));
    }
}

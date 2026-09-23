#nullable enable
using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Security;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using Duende.IdentityModel.OidcClient;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Jellyfin.Plugin.SSO_Auth.Config;

namespace Jellyfin.Plugin.SSO_Auth.Services;

public sealed class OidcAdapter(IHttpClientFactory clients)
{
    public OidcClient Client(OidConfig config, string callback, string? nonce = null)
    {
        if (!Uri.TryCreate(config.OidEndpoint?.Trim(), UriKind.Absolute, out var authority)
            || (authority.Scheme != "https" && !(config.DisableHttps && authority.Scheme == "http"))
            || string.IsNullOrWhiteSpace(config.OidClientId))
        {
            throw new SsoException("Configure a valid OIDC authority and client ID.");
        }

        var options = new OidcClientOptions
        {
            FilterClaims = false,
            IdentityTokenValidator = new OidcTokenValidator(nonce),
            Authority = authority.AbsoluteUri.TrimEnd('/'),
            ClientId = config.OidClientId.Trim(),
            ClientSecret = config.OidSecret,
            RedirectUri = callback,
            Scope = string.Join(" ", new[] { "openid", "profile" }.Concat(config.OidScopes).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()),
            DisablePushedAuthorization = config.DisablePushedAuthorization,
            LoadProfile = !config.DoNotLoadProfile,
            HttpClientFactory = _ => clients.CreateClient("sso-protocol"),
        };
        options.Policy.RequireIdentityTokenSignature = true;
        options.Policy.Discovery.AdditionalEndpointBaseAddresses.Add(authority.GetLeftPart(UriPartial.Authority));
        options.Policy.Discovery.RequireHttps = !config.DisableHttps;
        options.Policy.Discovery.ValidateEndpoints = !config.DoNotValidateEndpoints;
        options.Policy.Discovery.ValidateIssuerName = !config.DoNotValidateIssuerName;
        return new OidcClient(options);
    }

    public async Task<ExternalIdentity> Verify(LoginTransaction transaction, string response)
    {
        var config = (OidConfig)transaction.Settings;
        var result = await Client(config, transaction.Callback, transaction.Nonce).ProcessResponseAsync(response, transaction.OidState!).ConfigureAwait(false);
        if (result.IsError || string.IsNullOrWhiteSpace(result.IdentityToken))
        {
            throw new SsoException("The identity provider response could not be validated. Start again.");
        }

        // These claims come only from the library-validated identity token/principal.
        var subject = result.User.FindFirst("sub")?.Value;
        var issuer = result.User.FindFirst("iss")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(issuer))
        {
            throw new SsoException("The provider did not return a stable issuer and subject.");
        }

        var nameClaim = string.IsNullOrWhiteSpace(config.DefaultUsernameClaim) ? "preferred_username" : config.DefaultUsernameClaim.Trim();
        var name = result.User.FindFirst(nameClaim)?.Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = subject;
        }

        string? avatar = null;
        if (!string.IsNullOrWhiteSpace(config.AvatarUrlFormat))
        {
            avatar = result.User.Claims.Aggregate(config.AvatarUrlFormat, (value, claim) => value.Replace("@{" + claim.Type + "}", Uri.EscapeDataString(claim.Value), StringComparison.Ordinal));
            if (config.AvatarUrlFormat == "@{picture}")
            {
                avatar = result.User.FindFirst("picture")?.Value;
            }
        }

        return new ExternalIdentity(issuer, subject, name, IdentityPolicy.ReadRoles(result.User.Claims, config.RoleClaim), avatar);
    }
}

public sealed class SamlAdapter(LoginTransactions transactions, TimeProvider clock)
{
    private static Saml2Configuration Configuration(SamlConfig config)
    {
        if (!Uri.TryCreate(config.SamlEndpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https"
            || string.IsNullOrWhiteSpace(config.SamlIssuer) || string.IsNullOrWhiteSpace(config.SamlClientId)
            || string.IsNullOrWhiteSpace(config.SamlNameIdFormat) || config.SamlNameIdFormat.EndsWith(":transient", StringComparison.Ordinal))
        {
            throw new SsoException("Configure the SAML HTTPS endpoint, issuer, client ID, and stable NameID format. Legacy settings and links have been preserved.");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(config.SamlCertificate));
        }
        catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException)
        {
            throw new SsoException("The configured SAML signing certificate is invalid.");
        }

        var settings = new Saml2Configuration
        {
            Issuer = config.SamlClientId,
            AllowedIssuer = config.SamlIssuer,
            SingleSignOnDestination = endpoint,
            CertificateValidationMode = X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        // Trust the administrator-pinned signing certificate, not the XML's KeyInfo.
        settings.SignatureValidationCertificates.Add(certificate);
        settings.AllowedAudienceUris.Add(config.SamlClientId);
        return settings;
    }

    public (string Id, string Url) Start(SamlConfig config, string callback, string relayState)
    {
        var request = new Saml2AuthnRequest(Configuration(config))
        {
            AssertionConsumerServiceUrl = new Uri(callback),
            ProtocolBinding = new Uri("urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST"),
            NameIdPolicy = new NameIdPolicy { Format = config.SamlNameIdFormat, AllowCreate = true },
        };
        var binding = new Saml2RedirectBinding { RelayState = relayState };
        binding.Bind(request);
        return (request.IdAsString, binding.RedirectLocation.AbsoluteUri);
    }

    public ExternalIdentity Verify(LoginTransaction transaction, string encodedResponse)
    {
        if (encodedResponse.Length > 1_000_000)
        {
            throw new SsoException("SAML response is too large.");
        }

        var xml = Encoding.UTF8.GetString(Convert.FromBase64String(encodedResponse));
        using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 750_000 }))
        {
            while (reader.Read())
            {
                // Reject DTDs and over-sized documents before the protocol parser.
            }
        }

        var config = (SamlConfig)transaction.Settings;
        var response = new Saml2AuthnResponse(Configuration(config));
        var request = new ITfoxtec.Identity.Saml2.Http.HttpRequest
        {
            Method = "POST",
            Form = new NameValueCollection { { "SAMLResponse", encodedResponse } },
        };
        new Saml2PostBinding().Unbind(request, response);
        if (response.Status != Saml2StatusCodes.Success || response.InResponseTo?.Value != transaction.SamlRequestId
            || response.Destination?.OriginalString != transaction.Callback)
        {
            throw new SsoException("SAML response status, destination, or request correlation is invalid.");
        }

        var assertion = response.Saml2SecurityToken.Assertion;
        var now = clock.GetUtcNow().UtcDateTime;
        var conditions = assertion.Conditions;
        var confirmations = assertion.Subject.SubjectConfirmations;
        if (assertion.Issuer.Value != config.SamlIssuer || conditions?.NotBefore is null || conditions.NotOnOrAfter is null
            || conditions.NotBefore > now.AddMinutes(1) || conditions.NotOnOrAfter <= now
            || conditions.NotOnOrAfter > now.AddHours(1) || confirmations.Count != 1)
        {
            throw new SsoException("SAML assertion issuer or finite validity is invalid.");
        }

        var confirmation = confirmations.Single();
        var data = confirmation.SubjectConfirmationData;
        if (confirmation.Method.OriginalString != "urn:oasis:names:tc:SAML:2.0:cm:bearer"
            || data?.NotOnOrAfter is null || data.NotOnOrAfter <= now || data.NotBefore > now.AddMinutes(1)
            || data.Recipient?.OriginalString != transaction.Callback || data.InResponseTo?.Value != transaction.SamlRequestId
            || assertion.Subject.NameId?.Format?.OriginalString != config.SamlNameIdFormat
            || string.IsNullOrWhiteSpace(assertion.Subject.NameId.Value))
        {
            throw new SsoException("SAML subject, recipient, or request correlation is invalid.");
        }

        var replayKey = LoginTransactions.Hash(config.SamlIssuer + "\n" + assertion.Id.Value);
        if (!transactions.RememberReplay(replayKey, new DateTimeOffset(conditions.NotOnOrAfter.Value, TimeSpan.Zero)))
        {
            throw new SsoException("SAML assertion has already been used.");
        }

        var name = assertion.Subject.NameId.Value;
        var subject = JsonSerializer.Serialize(new[] { config.SamlNameIdFormat, assertion.Subject.NameId.NameQualifier ?? string.Empty, assertion.Subject.NameId.SPNameQualifier ?? string.Empty, name });
        return new ExternalIdentity(config.SamlIssuer, subject, name, IdentityPolicy.ReadRoles(response.ClaimsIdentity.Claims, "Role"));
    }
}

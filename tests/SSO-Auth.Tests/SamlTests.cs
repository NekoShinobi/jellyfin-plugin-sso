using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Services;
using Microsoft.IdentityModel.Tokens.Saml2;
using Xunit;

namespace SSO_Auth.Tests;

public class SamlTests : IDisposable
{
    private const string Callback = "https://jellyfin.example/sso/SAML/post/test";
    private const string Issuer = "https://idp.example/realm";
    private readonly RSA _key = RSA.Create(2048);
    private readonly X509Certificate2 _certificate;
    private readonly LoginTransactions _transactions = new(TimeProvider.System);

    public SamlTests()
    {
        _certificate = new CertificateRequest("CN=synthetic-saml", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private LoginTransaction Transaction()
    {
        var config = new SamlConfig
        {
            Enabled = true,
            SamlEndpoint = "https://idp.example/saml",
            SamlIssuer = Issuer,
            SamlClientId = "jellyfin",
            SamlCertificate = Convert.ToBase64String(_certificate.Export(X509ContentType.Cert)),
            Roles = ["allowed"],
        };
        return new("SAML", "test", Callback, "browser", null, ConfigurationMigration.Fingerprint(config), config, SamlRequestId: "_request");
    }

    private string Response(bool assertionSigned = false, Action<Saml2AuthnResponse>? change = null)
    {
        var response = new Saml2AuthnResponse(new Saml2Configuration
        {
            Issuer = Issuer,
            SigningCertificate = _certificate,
            AuthnResponseSignType = assertionSigned ? Saml2AuthnResponseSignTypes.SignAssertion : Saml2AuthnResponseSignTypes.SignResponse,
        })
        {
            Destination = new Uri(Callback),
            InResponseTo = new Saml2Id("_request"),
            Status = Saml2StatusCodes.Success,
            ClaimsIdentity = new ClaimsIdentity([new Claim("Role", "allowed")]),
            NameId = new Saml2NameIdentifier("stable-subject") { Format = new Uri("urn:oasis:names:tc:SAML:2.0:nameid-format:persistent") },
        };
        response.CreateSecurityToken("jellyfin", subjectConfirmationLifetime: 5, issuedTokenLifetime: 5);
        response.Saml2SecurityToken.Assertion.Conditions.NotBefore = DateTime.UtcNow.AddMinutes(-1);
        change?.Invoke(response);
        var binding = new Saml2PostBinding();
        binding.Bind(response);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(binding.XmlDocument.OuterXml));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsBothSignatureLayoutsAndRejectsReplay(bool assertionSigned)
    {
        var adapter = new SamlAdapter(_transactions, TimeProvider.System);
        var response = Response(assertionSigned);
        var identity = adapter.Verify(Transaction(), response);
        Assert.Equal(Issuer, identity.Issuer);
        Assert.Contains("allowed", identity.Roles);
        Assert.ThrowsAny<Exception>(() => adapter.Verify(Transaction(), response));
    }

    public static IEnumerable<object[]> InvalidCases => new[] { "issuer", "assertion-issuer", "audience", "destination", "recipient", "request", "subject-request", "expired", "future", "no-expiry", "no-subject-expiry", "transient", "no-role", "missing-audience", "subject-future", "subject-expired", "too-long", "status" }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void RejectsInvalidSignedAssertions(string invalid)
    {
        var response = Response(change: r =>
        {
            var assertion = r.Saml2SecurityToken.Assertion;
            var data = assertion.Subject.SubjectConfirmations.Single().SubjectConfirmationData;
            switch (invalid)
            {
                case "issuer": r.Issuer = "https://other.example"; break;
                case "assertion-issuer": assertion.Issuer = new Saml2NameIdentifier("https://other.example"); break;
                case "audience": assertion.Conditions.AudienceRestrictions.Clear(); assertion.Conditions.AudienceRestrictions.Add(new Saml2AudienceRestriction("other")); break;
                case "destination": r.Destination = new Uri("https://other.example"); break;
                case "recipient": data.Recipient = new Uri("https://other.example"); break;
                case "request": r.InResponseTo = new Saml2Id("_other"); break;
                case "subject-request": data.InResponseTo = new Saml2Id("_other"); break;
                case "expired": assertion.Conditions.NotOnOrAfter = DateTime.UtcNow.AddSeconds(-1); break;
                case "future": assertion.Conditions.NotBefore = DateTime.UtcNow.AddMinutes(4); break;
                case "no-expiry": assertion.Conditions.NotOnOrAfter = null; break;
                case "no-subject-expiry": data.NotOnOrAfter = null; break;
                case "transient": assertion.Subject.NameId.Format = new Uri("urn:oasis:names:tc:SAML:2.0:nameid-format:transient"); break;
                case "missing-audience": assertion.Conditions.AudienceRestrictions.Clear(); break;
                case "subject-future": data.NotBefore = DateTime.UtcNow.AddMinutes(4); break;
                case "subject-expired": data.NotOnOrAfter = DateTime.UtcNow.AddSeconds(-1); break;
                case "too-long": assertion.Conditions.NotOnOrAfter = DateTime.UtcNow.AddHours(2); break;
                case "status": r.Status = Saml2StatusCodes.Responder; break;
                case "no-role": foreach (var statement in assertion.Statements.OfType<Saml2AttributeStatement>().ToArray()) assertion.Statements.Remove(statement); break;
            }
        });
        Assert.ThrowsAny<Exception>(() =>
        {
            var identity = new SamlAdapter(_transactions, TimeProvider.System).Verify(Transaction(), response);
            IdentityPolicy.Admit(Transaction().Settings, identity.Roles);
        });
    }

    [Fact]
    public void TamperingAndDtdAreRejected()
    {
        var xml = Encoding.UTF8.GetString(Convert.FromBase64String(Response()));
        var tampered = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml.Replace("stable-subject", "attacker")));
        Assert.ThrowsAny<Exception>(() => new SamlAdapter(_transactions, TimeProvider.System).Verify(Transaction(), tampered));
        var dtd = Convert.ToBase64String(Encoding.UTF8.GetBytes("<!DOCTYPE Response [<!ENTITY x SYSTEM 'file:///tmp/no-such-sso-file'>]><Response>&x;</Response>"));
        Assert.ThrowsAny<Exception>(() => new SamlAdapter(_transactions, TimeProvider.System).Verify(Transaction(), dtd));
    }

    [Theory]
    [InlineData("missing-signature")]
    [InlineData("wrong-certificate")]
    [InlineData("duplicate-assertion")]
    [InlineData("wrapped-assertion")]
    public void RejectsMissingSignaturesUntrustedKeysAndWrapping(string invalid)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        document.LoadXml(Encoding.UTF8.GetString(Convert.FromBase64String(Response(assertionSigned: true))));
        var ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("ds", "http://www.w3.org/2000/09/xmldsig#");
        ns.AddNamespace("saml", "urn:oasis:names:tc:SAML:2.0:assertion");
        var transaction = Transaction();
        if (invalid == "missing-signature")
        {
            var signature = document.SelectSingleNode("//ds:Signature", ns)!;
            signature.ParentNode!.RemoveChild(signature);
        }
        else if (invalid == "wrong-certificate")
        {
            using var key = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=other", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            ((SamlConfig)transaction.Settings).SamlCertificate = Convert.ToBase64String(certificate.Export(X509ContentType.Cert));
        }
        else
        {
            var assertion = document.SelectSingleNode("//saml:Assertion", ns)!;
            var copy = assertion.CloneNode(true);
            copy.SelectSingleNode("saml:Subject/saml:NameID", ns)!.InnerText = "attacker";
            if (invalid == "duplicate-assertion") document.DocumentElement!.PrependChild(copy);
            else
            {
                var wrapper = document.CreateElement("wrapper");
                document.DocumentElement!.AppendChild(wrapper);
                wrapper.AppendChild(assertion);
                document.DocumentElement.PrependChild(copy);
            }
        }
        var response = Convert.ToBase64String(Encoding.UTF8.GetBytes(document.OuterXml));
        Assert.ThrowsAny<Exception>(() => new SamlAdapter(_transactions, TimeProvider.System).Verify(transaction, response));
    }

    public void Dispose()
    {
        _transactions.Dispose();
        _certificate.Dispose();
        _key.Dispose();
    }
}

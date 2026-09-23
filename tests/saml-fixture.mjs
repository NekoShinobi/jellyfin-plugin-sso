// Deterministic canonical XML for synthetic browser fixtures only.
// Production parsing and signature verification are performed by ITfoxtec.
import { createServer as createHttpsServer } from "node:https";
import { createHash, randomUUID, sign } from "node:crypto";
import { inflateRawSync } from "node:zlib";

export async function samlFixture(key, cert, gateway) {
  const fixture = {
    subject: "saml-browser-user",
    role: "allowed",
    correlation: "valid",
    lastForm: null,
  };
  const certificate = cert
    .toString()
    .replace(/-----[^-]+-----/g, "")
    .replace(/\s/g, "");
  const escape = (value) =>
    String(value)
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll('"', "&quot;");
  const ns = "urn:oasis:names:tc:SAML:2.0:assertion";
  const ds = "http://www.w3.org/2000/09/xmldsig#";
  const c14n = "http://www.w3.org/2001/10/xml-exc-c14n#";
  const server = createHttpsServer({ key, cert }, (req, res) => {
    try {
      const query = new URL(req.url, fixture.issuer).searchParams;
      const request = inflateRawSync(
        Buffer.from(query.get("SAMLRequest"), "base64"),
      ).toString();
      const requestId = /\bID="([^"]+)"/.exec(request)[1];
      const callback = /AssertionConsumerServiceURL="([^"]+)"/
        .exec(request)[1]
        .replaceAll("&amp;", "&");
      const id = "_" + randomUUID(),
        now = new Date().toISOString(),
        before = new Date(Date.now() - 30000).toISOString(),
        until = new Date(Date.now() + 300000).toISOString();
      const issuer = `<saml:Issuer>${escape(fixture.issuer)}</saml:Issuer>`;
      const start = `<saml:Assertion xmlns:saml="${ns}" ID="${id}" IssueInstant="${now}" Version="2.0">`;
      const body = `<saml:Subject><saml:NameID Format="urn:oasis:names:tc:SAML:2.0:nameid-format:persistent">${escape(fixture.subject)}</saml:NameID><saml:SubjectConfirmation Method="urn:oasis:names:tc:SAML:2.0:cm:bearer"><saml:SubjectConfirmationData InResponseTo="${escape(requestId)}" NotOnOrAfter="${until}" Recipient="${escape(callback)}"></saml:SubjectConfirmationData></saml:SubjectConfirmation></saml:Subject><saml:Conditions NotBefore="${before}" NotOnOrAfter="${until}"><saml:AudienceRestriction><saml:Audience>browser-saml-client</saml:Audience></saml:AudienceRestriction></saml:Conditions><saml:AuthnStatement AuthnInstant="${now}"><saml:AuthnContext><saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport</saml:AuthnContextClassRef></saml:AuthnContext></saml:AuthnStatement><saml:AttributeStatement><saml:Attribute Name="Role"><saml:AttributeValue>${escape(fixture.role)}</saml:AttributeValue></saml:Attribute></saml:AttributeStatement>`;
      const digest = createHash("sha256")
        .update(start + issuer + body + "</saml:Assertion>")
        .digest("base64");
      const signedInfo = `<SignedInfo xmlns="${ds}"><CanonicalizationMethod Algorithm="${c14n}"></CanonicalizationMethod><SignatureMethod Algorithm="http://www.w3.org/2001/04/xmldsig-more#rsa-sha256"></SignatureMethod><Reference URI="#${id}"><Transforms><Transform Algorithm="${ds}enveloped-signature"></Transform><Transform Algorithm="${c14n}"></Transform></Transforms><DigestMethod Algorithm="http://www.w3.org/2001/04/xmlenc#sha256"></DigestMethod><DigestValue>${digest}</DigestValue></Reference></SignedInfo>`;
      const signature = sign(
        "RSA-SHA256",
        Buffer.from(signedInfo),
        key,
      ).toString("base64");
      const xml = `<samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" ID="_${randomUUID()}" Version="2.0" IssueInstant="${now}" Destination="${escape(callback)}" InResponseTo="${escape(requestId)}"><saml:Issuer xmlns:saml="${ns}">${escape(fixture.issuer)}</saml:Issuer><samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status>${start}${issuer}<Signature xmlns="${ds}">${signedInfo}<SignatureValue>${signature}</SignatureValue><KeyInfo><X509Data><X509Certificate>${certificate}</X509Certificate></X509Data></KeyInfo></Signature>${body}</saml:Assertion></samlp:Response>`;
      const relay =
        fixture.correlation === "valid"
          ? query.get("RelayState")
          : fixture.correlation === "missing"
            ? ""
            : "tampered";
      const encoded = Buffer.from(xml).toString("base64");
      fixture.lastForm = { callback, encoded, relay };
      res.writeHead(200, { "Content-Type": "text/html" });
      res.end(
        `<form method="post" action="${escape(callback)}"><input name="SAMLResponse" value="${encoded}"><input name="RelayState" value="${escape(relay)}"></form><script>document.forms[0].submit()</script>`,
      );
    } catch {
      res.writeHead(400);
      res.end("Invalid synthetic request");
    }
  });
  await new Promise((resolve) => server.listen(0, "0.0.0.0", resolve));
  fixture.issuer = `https://${gateway}:${server.address().port}`;
  fixture.certificate = certificate;
  fixture.close = () => new Promise((resolve) => server.close(resolve));
  return fixture;
}

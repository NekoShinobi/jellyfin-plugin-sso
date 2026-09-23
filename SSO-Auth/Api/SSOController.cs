#nullable enable
using System;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SSO_Auth.Api;

[ApiController]
[Route("sso")]
public class SSOController(ProviderStore providers, LoginTransactions transactions, OidcAdapter oidc, SamlAdapter saml, AccountService accounts, IAuthorizationContext authorization, ILogger<SSOController> logger) : ControllerBase
{
    private string CookiePath => (Request.PathBase.Value ?? string.Empty) + "/sso";

    [HttpGet("{mode}/start/{provider}")]
    [HttpGet("{mode}/p/{provider}")]
    public Task<ActionResult> Start(string mode, string provider, [FromQuery] bool isLinking = false) => Guard(async () =>
    {
        if (isLinking)
        {
            // Old bookmarks still reach the authenticated linking UI, never an anonymous link flow.
            return Redirect(Request.PathBase + "/SSOViews/linking");
        }

        return Redirect(await Begin(Mode(mode), provider, null).ConfigureAwait(false));
    });

    [Authorize]
    [HttpPost("{mode}/start/{provider}")]
    public Task<ActionResult> StartLink(string mode, string provider) => Guard(async () =>
    {
        var auth = await authorization.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (auth.User is null || !auth.User.EnableUserPreferenceAccess)
        {
            throw new SsoException("Sign in to the Jellyfin account you want to link first.", 403);
        }

        var url = await Begin(Mode(mode), provider, auth.User.Id).ConfigureAwait(false);
        return Ok(new { Url = url });
    });

    private async Task<string> Begin(string mode, string provider, Guid? target)
    {
        var config = providers.Get(mode, provider);
        var legacy = Request.Path.Value!.Contains("/p/", StringComparison.OrdinalIgnoreCase);
        var suffix = mode == "OID" ? (legacy ? "r" : "redirect") : (legacy ? "p" : "post");
        var callback = BaseUrl(config) + "/sso/" + mode + "/" + suffix + "/" + Uri.EscapeDataString(provider);
        var browser = LoginTransactions.Secret();
        var transaction = new LoginTransaction(mode, provider, callback, LoginTransactions.Hash(browser), target, ConfigurationMigration.Fingerprint(config), config);
        string key;
        string url;
        if (mode == "OID")
        {
            var nonce = LoginTransactions.Secret();
            var state = await oidc.Client((OidConfig)config, callback, nonce).PrepareLoginAsync(new Duende.IdentityModel.Client.Parameters { { "nonce", nonce } }).ConfigureAwait(false);
            if (state.IsError)
            {
                throw new SsoException("Unable to start provider authentication. Check discovery and client settings.");
            }

            key = state.State;
            url = state.StartUrl;
            transaction = transaction with { OidState = state, Nonce = nonce };
        }
        else
        {
            key = LoginTransactions.Secret();
            var request = saml.Start((SamlConfig)config, callback, key);
            transaction = transaction with { SamlRequestId = request.Id };
            url = request.Url;
        }

        transactions.Add("flow:" + key, transaction, TimeSpan.FromMinutes(10));
        SetCookie(key, browser, callback.StartsWith("https:", StringComparison.Ordinal), TimeSpan.FromMinutes(10));
        return url;
    }

    [HttpGet("OID/redirect/{provider}")]
    [HttpGet("OID/r/{provider}")]
    public Task<ActionResult> OidPost(string provider, [FromQuery] string state) => Guard(async () =>
    {
        var transaction = TakeFlow("OID", provider, state);
        var identity = await oidc.Verify(transaction, Request.QueryString.Value!).ConfigureAwait(false);
        return CompletePage(transaction, identity, state);
    });

    [HttpPost("SAML/post/{provider}")]
    [HttpPost("SAML/p/{provider}")]
    [RequestSizeLimit(1_100_000)]
    public Task<ActionResult> SamlPost(string provider) => Guard(async () =>
    {
        var form = await Request.ReadFormAsync().ConfigureAwait(false);
        var key = form["RelayState"].ToString();
        var transaction = TakeFlow("SAML", provider, key);
        var identity = saml.Verify(transaction, form["SAMLResponse"].ToString());
        return CompletePage(transaction, identity, key);
    });

    private LoginTransaction TakeFlow(string mode, string provider, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new SsoException("Missing authentication correlation. Start sign-in from Jellyfin.");
        }

        var transaction = transactions.Take<LoginTransaction>("flow:" + key, t => t.Mode == mode && t.Provider == provider && MatchesBrowser(key, t));
        providers.EnsureUnchanged(transaction);
        var callback = BaseUrl(transaction.Settings) + Request.Path.ToUriComponent();
        if (!string.Equals(callback, transaction.Callback, StringComparison.Ordinal))
        {
            throw new SsoException("The callback URL differs from the URL used to start sign-in.");
        }

        return transaction;
    }

    private ActionResult CompletePage(LoginTransaction transaction, ExternalIdentity identity, string flowKey)
    {
        IdentityPolicy.Admit(transaction.Settings, identity.Roles);
        var code = LoginTransactions.Secret();
        transactions.Add("complete:" + code, new LoginCompletion(transaction, identity), TimeSpan.FromMinutes(2));
        var browser = Request.Cookies[CookieName(flowKey)]!;
        DeleteCookie(flowKey);
        SetCookie(code, browser, transaction.Callback.StartsWith("https:", StringComparison.Ordinal), TimeSpan.FromMinutes(2));
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'self'; base-uri 'none'; frame-ancestors 'none'";
        return Content(WebResponse.Completion(Request.PathBase.Value ?? string.Empty, transaction.Mode, transaction.Provider, code, transaction.TargetUser), MediaTypeNames.Text.Html);
    }

    [HttpPost("{mode}/Auth/{provider}")]
    public Task<ActionResult> Authenticate(string mode, string provider, [FromBody] AuthResponse request) => Guard(async () =>
    {
        var completion = TakeCompletion(Mode(mode), provider, request.Data, null);
        var result = await accounts.Login(completion, request, HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty).ConfigureAwait(false);
        return Ok(result);
    });

    [Authorize]
    [HttpPost("{mode}/Link/{provider}/{jellyfinUserId:guid}")]
    public Task<ActionResult> Link(string mode, string provider, Guid jellyfinUserId, [FromBody] AuthResponse request) => Guard(async () =>
    {
        await RequireOwnAccount(jellyfinUserId).ConfigureAwait(false);
        var completion = TakeCompletion(Mode(mode), provider, request.Data, jellyfinUserId);
        await accounts.Link(completion, jellyfinUserId).ConfigureAwait(false);
        return NoContent();
    });

    private LoginCompletion TakeCompletion(string mode, string provider, string code, Guid? target)
    {
        var completion = transactions.Take<LoginCompletion>("complete:" + code, c => c.Transaction.Mode == mode && c.Transaction.Provider == provider && c.Transaction.TargetUser == target && MatchesBrowser(code, c.Transaction));
        DeleteCookie(code);
        providers.EnsureUnchanged(completion.Transaction);
        return completion;
    }

    [Authorize]
    [HttpGet("{mode}/links/{jellyfinUserId:guid}")]
    public Task<ActionResult> Links(string mode, Guid jellyfinUserId) => Guard(async () =>
    {
        await RequireOwnAccount(jellyfinUserId).ConfigureAwait(false);
        var config = providers.Snapshot();
        var all = Mode(mode) == "OID" ? config.OidConfigs.Select(p => (p.Key, Config: (ProviderConfig)p.Value)) : config.SamlConfigs.Select(p => (p.Key, Config: (ProviderConfig)p.Value));
        return Ok(all.ToDictionary(p => p.Key, p => p.Config.SubjectLinks.Concat(p.Config.CanonicalLinks).Where(l => l.Value == jellyfinUserId).Select(l => l.Key).Distinct().ToArray()));
    });

    [Authorize]
    [HttpDelete("{mode}/Link/{provider}/{jellyfinUserId:guid}/{canonicalName}")]
    public Task<ActionResult> Unlink(string mode, string provider, Guid jellyfinUserId, string canonicalName) => Guard(async () =>
    {
        await RequireOwnAccount(jellyfinUserId).ConfigureAwait(false);
        providers.Edit(c =>
        {
            var config = ProviderStore.Find(c, Mode(mode), provider);
            foreach (var links in new[] { config.SubjectLinks, config.CanonicalLinks })
            {
                if (links.TryGetValue(canonicalName, out var target) && target == jellyfinUserId)
                {
                    links.Remove(canonicalName);
                }
            }
        });
        return NoContent();
    });

    private async Task RequireOwnAccount(Guid target)
    {
        var auth = await authorization.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (auth.User is null || auth.User.Id != target || !auth.User.EnableUserPreferenceAccess)
        {
            throw new SsoException("Sign in to the account whose links you want to change.", 403);
        }
    }

    [HttpGet("{mode}/GetNames")]
    public Task<ActionResult> Names(string mode) => Guard(() =>
    {
        var config = providers.Snapshot();
        return Ok(Mode(mode) == "OID" ? config.OidConfigs.Where(p => p.Value.Enabled).Select(p => p.Key) : config.SamlConfigs.Where(p => p.Value.Enabled).Select(p => p.Key));
    });

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("{mode}/Get")]
    public Task<ActionResult> Get(string mode) => Guard(() =>
    {
        Response.Headers.CacheControl = "no-store";
        var config = providers.Snapshot();
        return Mode(mode) == "OID" ? Ok(config.OidConfigs) : Ok(config.SamlConfigs);
    });

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("OID/Add/{provider}")]
    public Task<ActionResult> AddOid(string provider, [FromBody] OidConfig config) => Guard(() =>
    {
        providers.Edit(c => c.OidConfigs[provider] = config);
        return NoContent();
    });

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("SAML/Add/{provider}")]
    public Task<ActionResult> AddSaml(string provider, [FromBody] SamlConfig config) => Guard(() =>
    {
        providers.Edit(c => c.SamlConfigs[provider] = config);
        return NoContent();
    });

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpDelete("{mode}/Del/{provider}")]
    [HttpGet("{mode}/Del/{provider}")]
    public Task<ActionResult> Delete(string mode, string provider) => Guard(() =>
    {
        providers.Edit(c =>
        {
            if (Mode(mode) == "OID")
            {
                c.OidConfigs.Remove(provider);
            }
            else
            {
                c.SamlConfigs.Remove(provider);
            }
        });
        return NoContent();
    });

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Permissions")]
    public ActionResult Permissions() => Ok(PermissionPolicy.Catalogue);

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Permissions/Preview")]
    public Task<ActionResult> PreviewPermissions([FromBody] PermissionPreviewRequest request) => Guard(() =>
    {
        Response.Headers.CacheControl = "no-store";
        return Task.FromResult<ActionResult>(Ok(accounts.PreviewPermissions(request)));
    });

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("OID/States")]
    public ActionResult States() => Ok(new { ActiveTransactions = transactions.Count });

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Unregister/{username}")]
    public Task<ActionResult> Unregister(string username, [FromBody] string provider) => Guard(async () =>
    {
        await accounts.Unregister(username, provider).ConfigureAwait(false);
        return NoContent();
    });

    private static string Mode(string mode) => mode.ToUpperInvariant() switch
    {
        "OID" => "OID",
        "SAML" => "SAML",
        _ => throw new SsoException("Unknown authentication protocol.", 404),
    };

    private string BaseUrl(ProviderConfig config)
    {
        var scheme = string.IsNullOrEmpty(config.SchemeOverride) ? Request.Scheme : config.SchemeOverride;
        if (scheme is not ("http" or "https"))
        {
            throw new SsoException("Invalid public URL scheme.");
        }

        return new UriBuilder(scheme, Request.Host.Host, config.PortOverride ?? Request.Host.Port ?? (scheme == "https" ? 443 : 80), Request.PathBase).Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string CookieName(string key) => "sso-" + LoginTransactions.Hash(key);

    private bool MatchesBrowser(string key, LoginTransaction transaction) => Request.Cookies.TryGetValue(CookieName(key), out var browser) && LoginTransactions.Hash(browser) == transaction.BrowserHash;

    private void DeleteCookie(string key) => Response.Cookies.Delete(CookieName(key), new CookieOptions { Path = CookiePath });

    private void SetCookie(string key, string secret, bool secure, TimeSpan age) => Response.Cookies.Append(CookieName(key), secret, new CookieOptions { HttpOnly = true, Secure = secure, SameSite = secure ? SameSiteMode.None : SameSiteMode.Lax, Path = CookiePath, MaxAge = age, IsEssential = true });

    private Task<ActionResult> Guard(Func<ActionResult> action) => Guard(() => Task.FromResult(action()));

    private async Task<ActionResult> Guard(Func<Task<ActionResult>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (SsoException exception)
        {
            logger.LogWarning("SSO stage {Stage} rejected ({Status}): {Reason}; correlation {Correlation}", ControllerContext.ActionDescriptor?.ActionName ?? "request", exception.Status, exception.Message, HttpContext.TraceIdentifier);
            return StatusCode(exception.Status, new { Error = exception.Message });
        }
        catch (Exception)
        {
            logger.LogWarning("SSO stage {Stage}: provider validation or transport failed; correlation {Correlation}", ControllerContext.ActionDescriptor?.ActionName ?? "request", HttpContext.TraceIdentifier);
            return BadRequest(new { Error = "Sign-in could not be completed. Check provider settings or start again." });
        }
    }
}

public class AuthResponse
{
    public string DeviceID { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string AppName { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public string Data { get; set; } = string.Empty;
}

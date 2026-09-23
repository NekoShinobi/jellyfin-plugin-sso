#nullable enable
using System.Linq;
using MediaBrowser.Model;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SSO_Auth.Views;

[ApiController]
[Route("SSOViews")]
public class SSOViewsController : ControllerBase
{
    [HttpGet("{viewName}")]
    public ActionResult GetView(string viewName)
    {
        var plugin = SSOPlugin.Instance;
        var view = plugin.GetViews().FirstOrDefault(p => p.Name == viewName);
        if (view is null)
        {
            return NotFound();
        }

        var stream = plugin.GetType().Assembly.GetManifestResourceStream(view.EmbeddedResourcePath);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'self'; base-uri 'none'; frame-ancestors 'none'";
        return File(stream, MimeTypes.GetMimeType(view.EmbeddedResourcePath));
    }
}

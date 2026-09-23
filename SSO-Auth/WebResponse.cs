#nullable enable
using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SSO_Auth;

public static class WebResponse
{
    private static readonly string CompletionTemplate = LoadCompletionTemplate();

    private static string LoadCompletionTemplate()
    {
        using var stream = typeof(WebResponse).Assembly.GetManifestResourceStream("Jellyfin.Plugin.SSO_Auth.Views.complete.html")
            ?? throw new InvalidOperationException("The sign-in completion template is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Completion(string basePath, string mode, string provider, string code, Guid? target)
    {
        var data = JsonSerializer.Serialize(new { basePath, mode, provider, code, target });
        var encodedBasePath = WebUtility.HtmlEncode(basePath);
        // Replace in one pass so inserted values cannot introduce template substitutions.
        return Regex.Replace(CompletionTemplate, @"\{\{(basePath|data)\}\}", match => match.Groups[1].Value == "data" ? data : encodedBasePath);
    }
}

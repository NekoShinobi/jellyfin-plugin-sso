#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SSO_Auth.Services;

// Adds the Account connections entry to the web client's user settings menu when the optional
// File Transformation plugin is installed. It is used through reflection, so it stays optional.
public sealed class WebMenuIntegration(ILogger<WebMenuIntegration> logger) : IHostedService
{
    private const string MenuScript = "<script defer src=\"../SSOViews/menu.js\"></script>";

    private static readonly Guid TransformationId = Guid.Parse("4b5c0d8e-7a0f-4c55-9e1a-5f0d3a6c2b71");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var register = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(assembly => assembly.GetName().Name == "Jellyfin.Plugin.FileTransformation")
            ?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")
            ?.GetMethod("RegisterTransformation");
        if (register is null)
        {
            logger.LogInformation("File Transformation is not installed; the user settings menu has no Account connections entry.");
            return Task.CompletedTask;
        }

        try
        {
            // The payload is a Newtonsoft JObject from File Transformation's own load context.
            var payloadType = register.GetParameters().Single().ParameterType;
            var parse = payloadType.GetMethod("Parse", [typeof(string)]) ?? throw new MissingMethodException(payloadType.FullName, "Parse");
            var payload = parse.Invoke(null, [JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["id"] = TransformationId.ToString(),
                ["fileNamePattern"] = "index\\.html$",
                ["callbackAssembly"] = typeof(WebMenuIntegration).Assembly.FullName!,
                ["callbackClass"] = typeof(WebMenuIntegration).FullName!,
                ["callbackMethod"] = nameof(AddMenuScript),
            })]);
            register.Invoke(null, [payload]);
            logger.LogInformation("Added Account connections to the user settings menu through File Transformation.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "File Transformation rejected the Account connections menu entry.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Invoked by File Transformation with the current index.html contents.
    public static string AddMenuScript(WebFileContents file)
    {
        var contents = file.Contents ?? string.Empty;
        if (contents.Contains("SSOViews/menu.js", StringComparison.Ordinal))
        {
            return contents;
        }

        var head = contents.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return head < 0 ? contents : contents.Insert(head, MenuScript);
    }
}

public sealed class WebFileContents
{
    public string? Contents { get; set; }
}

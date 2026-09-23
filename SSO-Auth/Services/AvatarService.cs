#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SSO_Auth.Services;

public interface IAvatarService
{
    Task Refresh(Guid userId, string? url, Func<string, Task> saveProfileImage);
}

public sealed class AvatarService(IImageEncoder decoder, IProviderManager images, IServerConfigurationManager server, IHttpClientFactory clients, ILogger<AvatarService> logger) : IAvatarService
{
    private readonly object _refreshGate = new();
    private readonly Dictionary<Guid, RefreshLock> _refreshes = new();

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return (bytes[0] & 0xe0) == 0x20
                && !(bytes[0] == 0x20 && bytes[1] == 0x02)
                && !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] < 2 || (bytes[2] == 0x0d && bytes[3] == 0xb8)))
                && !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
        }

        return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127 && bytes[0] < 224
            && !(bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            && !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 || (bytes[1] == 88 && bytes[2] == 99)))
            && !(bytes[0] == 198 && (bytes[1] is 18 or 19 || (bytes[1] == 51 && bytes[2] == 100)))
            && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }

    public async Task Refresh(Guid userId, string? url, Func<string, Task> saveProfileImage)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        RefreshLock entry;
        lock (_refreshGate)
        {
            if (!_refreshes.TryGetValue(userId, out entry!))
            {
                entry = new RefreshLock();
                _refreshes.Add(userId, entry);
            }

            entry.Users++;
        }

        await entry.Semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await RefreshCore(userId, url, saveProfileImage).ConfigureAwait(false);
        }
        finally
        {
            entry.Semaphore.Release();
            lock (_refreshGate)
            {
                if (--entry.Users == 0)
                {
                    _refreshes.Remove(userId);
                    entry.Semaphore.Dispose();
                }
            }
        }
    }

    private async Task RefreshCore(Guid userId, string url, Func<string, Task> saveProfileImage)
    {
        try
        {
            if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
            {
                throw new InvalidOperationException();
            }

            using var client = clients.CreateClient("sso-avatar");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            const int limit = 2 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit)
            {
                throw new InvalidOperationException();
            }

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > limit)
                {
                    throw new InvalidOperationException();
                }

                output.Write(buffer, 0, count);
            }

            var bytes = output.ToArray();
            var format = bytes.Length > 12 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                ? (Mime: "image/png", Extension: "png")
                : bytes.Length > 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255
                    ? (Mime: "image/jpeg", Extension: "jpg")
                    : bytes.Length > 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP"
                        ? (Mime: "image/webp", Extension: "webp")
                        : throw new InvalidOperationException();
            var directory = Path.Combine(server.ApplicationPaths.UserConfigurationDirectoryPath, userId.ToString("N"));
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + "." + format.Extension);
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, timeout.Token).ConfigureAwait(false);
                var dimensions = decoder.GetImageSize(temporary);
                if (dimensions.Width <= 0 || dimensions.Height <= 0 || dimensions.Width > 4096 || dimensions.Height > 4096)
                {
                    throw new InvalidOperationException();
                }
            }
            finally
            {
                File.Delete(temporary);
            }

            var path = Path.Combine(directory, "profile." + format.Extension);
            output.Position = 0;
            await images.SaveImage(output, format.Mime, path).ConfigureAwait(false);
            await saveProfileImage(path).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // No URL, claims, or response body in logs; optional images never fail login.
            logger.LogWarning("SSO avatar refresh failed validation or download.");
        }
    }

    public static SocketsHttpHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectCallback = async (context, cancellation) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation).ConfigureAwait(false);
            if (addresses.Length == 0 || addresses.Any(a => !IsPublicAddress(a)))
            {
                throw new HttpRequestException("Avatar address is not public.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), cancellation).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private sealed class RefreshLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }
}

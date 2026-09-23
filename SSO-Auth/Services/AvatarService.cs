#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
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

    // Existing implementations can keep download-only behavior.
    Task RepairLegacy(Guid userId, string? currentPath, Func<string, Task<bool>> trySaveProfileImage) => Task.CompletedTask;
}

public sealed class AvatarService(IImageEncoder decoder, IProviderManager images, IServerConfigurationManager server, IHttpClientFactory clients, ILogger<AvatarService> logger) : IAvatarService
{
    private const int MaxImageBytes = 2 * 1024 * 1024;
    private const int MaxCachedUsers = 1024;
    private readonly Dictionary<Guid, CachedImage> _cache = new();
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

        await ForUser(userId, () => RefreshCore(userId, url, saveProfileImage)).ConfigureAwait(false);
    }

    public async Task RepairLegacy(Guid userId, string? currentPath, Func<string, Task<bool>> trySaveProfileImage)
    {
        if (string.IsNullOrEmpty(currentPath)
            || Path.GetFileName(currentPath).ToLowerInvariant() is not ("profilepng" or "profilejpg" or "profilejpeg" or "profilewebp"))
        {
            return;
        }

        await ForUser(userId, () => RepairLegacyCore(userId, currentPath, trySaveProfileImage)).ConfigureAwait(false);
    }

    private async Task ForUser(Guid userId, Func<Task> action)
    {
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
            await action().ConfigureAwait(false);
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
        string? uncommitted = null;
        try
        {
            if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
            {
                throw new InvalidOperationException();
            }

            CachedImage? cached;
            lock (_refreshGate)
            {
                _cache.TryGetValue(userId, out cached);
            }

            if (cached?.Url != url || !File.Exists(cached.Path))
            {
                cached = null;
            }

            using var client = clients.CreateClient("sso-avatar");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (cached?.ETag is not null)
            {
                request.Headers.IfNoneMatch.Add(cached.ETag);
            }
            else if (cached?.LastModified is not null)
            {
                request.Headers.IfModifiedSince = cached.LastModified;
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null
                && (cached.ETag is not null || cached.LastModified is not null))
            {
                var revalidated = cached with
                {
                    ETag = response.Headers.ETag ?? cached.ETag,
                    LastModified = response.Content.Headers.LastModified ?? cached.LastModified,
                };
                Remember(userId, revalidated, response);
                return;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxImageBytes)
            {
                throw new InvalidOperationException();
            }

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var bytes = await ReadImage(input, timeout.Token).ConfigureAwait(false);
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            if (cached?.Digest == digest)
            {
                Remember(userId, cached with { ETag = response.Headers.ETag, LastModified = response.Content.Headers.LastModified }, response);
                return;
            }

            var format = ImageFormat(bytes);
            var directory = Path.Combine(server.ApplicationPaths.UserConfigurationDirectoryPath, userId.ToString("N"));
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                throw new InvalidOperationException();
            }

            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + "." + format.Extension);
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, timeout.Token).ConfigureAwait(false);
                ValidateImage(temporary);
            }
            finally
            {
                File.Delete(temporary);
            }

            // Never overwrite the currently referenced image, even if SaveImage or
            // the metadata commit fails midway. Publish the new reference last.
            var path = Path.Combine(directory, "profile-sso-" + Guid.NewGuid().ToString("N") + "." + format.Extension);
            uncommitted = path;
            using var output = new MemoryStream(bytes, writable: false);
            await images.SaveImage(output, format.Mime, path).ConfigureAwait(false);
            await saveProfileImage(path).ConfigureAwait(false);
            uncommitted = null;
            Remember(userId, new CachedImage(url, path, digest, response.Headers.ETag, response.Content.Headers.LastModified), response);
            // Only files owned by this refresh implementation are eligible for cleanup.
            foreach (var previous in Directory.EnumerateFiles(directory, "profile-sso-*"))
            {
                if (previous != path)
                {
                    File.Delete(previous);
                }
            }
        }
        catch (Exception)
        {
            // No URL, claims, or response body in logs; optional images never fail login.
            logger.LogWarning("SSO avatar refresh failed validation, download, or persistence.");
        }
        finally
        {
            if (uncommitted is not null)
            {
                try
                {
                    File.Delete(uncommitted);
                }
                catch (Exception)
                {
                    logger.LogWarning("SSO could not remove an uncommitted avatar replacement.");
                }
            }
        }
    }

    private void Remember(Guid userId, CachedImage image, HttpResponseMessage response)
    {
        lock (_refreshGate)
        {
            _cache.Remove(userId);
            if (response.Headers.CacheControl?.NoStore == true)
            {
                return;
            }

            // Validators are an optional, bounded process-local optimization.
            if (_cache.Count >= MaxCachedUsers)
            {
                _cache.Remove(_cache.Keys.First());
            }

            _cache.Add(userId, image);
        }
    }

    private async Task RepairLegacyCore(Guid userId, string currentPath, Func<string, Task<bool>> trySaveProfileImage)
    {
        string? repaired = null;
        var created = false;
        try
        {
            var root = Path.GetFullPath(server.ApplicationPaths.UserConfigurationDirectoryPath);
            var source = Path.GetFullPath(currentPath);
            var relative = Path.GetRelativePath(root, source);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            // Legacy avatars were directly inside a user folder. Never follow a
            // stored path outside that root or through a file/directory symlink.
            if (Path.IsPathRooted(relative) || parts.Length != 2 || parts[0] is "." or ".."
                || (File.GetAttributes(Path.GetDirectoryName(source)!) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            byte[] bytes;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true))
            {
                bytes = await ReadImage(input, timeout.Token).ConfigureAwait(false);
            }

            var format = ImageFormat(bytes);
            var directory = Path.Combine(root, userId.ToString("N"));
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                return;
            }

            Directory.CreateDirectory(directory);
            repaired = Path.Combine(directory, "profile-recovered-" + Guid.NewGuid().ToString("N") + "." + format.Extension);
            await using (var output = new FileStream(repaired, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                created = true;
                await output.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            }

            ValidateImage(repaired);
            if (await trySaveProfileImage(repaired).ConfigureAwait(false))
            {
                // Keep the original for recovery. Only the account's reference changes.
                repaired = null;
            }
        }
        catch (Exception)
        {
            logger.LogWarning("SSO legacy avatar recovery failed validation or persistence; the original image was preserved.");
        }
        finally
        {
            if (created && repaired is not null)
            {
                try
                {
                    File.Delete(repaired);
                }
                catch (Exception)
                {
                    logger.LogWarning("SSO could not remove an unused avatar recovery copy.");
                }
            }
        }
    }

    private static async Task<byte[]> ReadImage(Stream input, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > MaxImageBytes)
            {
                throw new InvalidOperationException();
            }

            output.Write(buffer, 0, count);
        }

        return output.ToArray();
    }

    private static (string Mime, string Extension) ImageFormat(byte[] bytes) =>
        bytes.Length > 12 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            ? ("image/png", "png")
            : bytes.Length > 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255
                ? ("image/jpeg", "jpg")
                : bytes.Length > 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP"
                    ? ("image/webp", "webp")
                    : throw new InvalidOperationException();

    private void ValidateImage(string path)
    {
        var dimensions = decoder.GetImageSize(path);
        if (dimensions.Width <= 0 || dimensions.Height <= 0 || dimensions.Width > 4096 || dimensions.Height > 4096)
        {
            throw new InvalidOperationException();
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

    private sealed record CachedImage(string Url, string Path, string Digest, EntityTagHeaderValue? ETag, DateTimeOffset? LastModified);

    private sealed class RefreshLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }
}

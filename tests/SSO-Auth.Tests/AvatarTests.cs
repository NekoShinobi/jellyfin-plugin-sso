using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Jellyfin.Plugin.SSO_Auth.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace SSO_Auth.Tests;

public class AvatarTests
{
    private sealed class Download : HttpMessageHandler, IHttpClientFactory
    {
        public readonly ConcurrentQueue<string> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond;
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("sso-avatar", name);
            return new(this, disposeHandler: false);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/failure") throw new HttpRequestException("Synthetic failure");
            if (Respond is not null) return Task.FromResult(Respond(request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0, 0]),
            });
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "sso-avatar-" + Guid.NewGuid().ToString("N"));
        public readonly Download Http = new();
        public readonly AvatarService Service;
        public readonly Mock<IImageEncoder> Decoder = new();
        public readonly Mock<IProviderManager> Images = new();

        public Fixture()
        {
            Decoder.Setup(d => d.GetImageSize(It.IsAny<string>())).Returns(new ImageDimensions(1, 1));
            Images.Setup(i => i.SaveImage(It.IsAny<Stream>(), "image/png", It.IsAny<string>())).Returns(async (Stream input, string mime, string path) =>
            {
                await using var output = File.Create(path);
                await input.CopyToAsync(output);
            });
            var server = new Mock<IServerConfigurationManager>();
            server.Setup(s => s.ApplicationPaths.UserConfigurationDirectoryPath).Returns(Directory);
            Service = new(Decoder.Object, Images.Object, server.Object, Http, NullLogger<AvatarService>.Instance);
        }

        public void Dispose()
        {
            Http.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    [Fact]
    public async Task RefreshesSerializeForOneUserWhileAnotherUserProceeds()
    {
        using var f = new Fixture();
        var user = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = new ConcurrentQueue<string>();
        var first = f.Service.Refresh(user, "https://avatar.test/first", async path =>
        {
            commits.Enqueue("first");
            entered.SetResult();
            await release.Task;
        });
        Task second = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = f.Service.Refresh(user, "https://avatar.test/second", path => { commits.Enqueue("second"); return Task.CompletedTask; });
            await f.Service.Refresh(Guid.NewGuid(), "https://avatar.test/other", path => { commits.Enqueue("other"); return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(second.IsCompleted);
            Assert.DoesNotContain("/second", f.Http.Requests);
            Assert.Equal(new[] { "first", "other" }, commits.ToArray());
            release.SetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "first", "other", "second" }, commits.ToArray());
            Assert.Contains("/second", f.Http.Requests);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDownloadsOrCommitsDoNotPoisonLaterRefreshes(bool commitFailure)
    {
        using var f = new Fixture();
        var user = Guid.NewGuid();
        var commits = 0;
        await f.Service.Refresh(user, "https://avatar.test/" + (commitFailure ? "image" : "failure"), _ => throw new IOException("Synthetic persistence failure"));
        await f.Service.Refresh(user, "https://avatar.test/retry", _ => { commits++; return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, commits);
    }

    private static HttpResponseMessage Image(byte variant = 0, string? etag = "\"v1\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0, variant]),
        };
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidatorsAvoidDownloadingAndRewritingUnchangedImages(bool etag)
    {
        using var f = new Fixture();
        var user = Guid.NewGuid();
        var modified = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var calls = 0;
        var validators = new List<(string? ETag, DateTimeOffset? Modified)>();
        f.Http.Respond = request =>
        {
            validators.Add((request.Headers.IfNoneMatch.FirstOrDefault()?.ToString(), request.Headers.IfModifiedSince));
            if (++calls == 1)
            {
                Assert.Empty(request.Headers.IfNoneMatch);
                Assert.Null(request.Headers.IfModifiedSince);
                var response = Image(etag: etag ? "W/\"v1\"" : null);
                response.Content.Headers.LastModified = modified;
                return response;
            }
            if (etag)
            {
                Assert.Equal("W/\"v1\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
                Assert.Null(request.Headers.IfModifiedSince);
            }
            else Assert.Equal(modified, request.Headers.IfModifiedSince);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        };
        var commits = 0;
        for (var i = 0; i < 3; i++)
            await f.Service.Refresh(user, "https://avatar.test/image", _ => { commits++; return Task.CompletedTask; });
        Assert.Equal(3, calls);
        Assert.Equal((null, (DateTimeOffset?)null), validators[0]);
        Assert.All(validators.Skip(1), value => Assert.Equal(etag ? ("W/\"v1\"", (DateTimeOffset?)null) : (null, modified), value));
        Assert.Equal(1, commits);
        f.Decoder.Verify(d => d.GetImageSize(It.IsAny<string>()), Times.Once);
        f.Images.Verify(i => i.SaveImage(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task IdenticalBytesWithoutValidatorsAvoidImageAndMetadataWrites()
    {
        using var f = new Fixture();
        f.Http.Respond = _ => Image(etag: null);
        var user = Guid.NewGuid();
        var commits = 0;
        for (var i = 0; i < 2; i++)
            await f.Service.Refresh(user, "https://avatar.test/image", _ => { commits++; return Task.CompletedTask; });
        Assert.Equal(2, f.Http.Requests.Count);
        Assert.Equal(1, commits);
        f.Decoder.Verify(d => d.GetImageSize(It.IsAny<string>()), Times.Once);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("user")]
    [InlineData("missing")]
    [InlineData("no-store")]
    public async Task ValidatorsAreOnlyReusedForTheSameUserUrlAndExistingCacheableImage(string change)
    {
        using var f = new Fixture();
        var user = Guid.NewGuid();
        string? path = null;
        f.Http.Respond = _ =>
        {
            var response = Image();
            if (change == "no-store") response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        };
        await f.Service.Refresh(user, "https://avatar.test/image", value => { path = value; return Task.CompletedTask; });
        Assert.NotNull(path);
        if (change == "missing") File.Delete(path);
        f.Http.Respond = request =>
        {
            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
            return Image();
        };
        var committed = false;
        await f.Service.Refresh(change == "user" ? Guid.NewGuid() : user,
            change == "url" ? "https://avatar.test/changed" : "https://avatar.test/image",
            _ => { committed = true; return Task.CompletedTask; });
        Assert.True(committed);
    }

    [Theory]
    [InlineData("download")]
    [InlineData("decode")]
    [InlineData("write")]
    [InlineData("commit")]
    public async Task FailedReplacementPreservesPreviousUsableImageAndValidators(string failure)
    {
        using var f = new Fixture();
        var user = Guid.NewGuid();
        string? current = null;
        f.Http.Respond = _ => Image();
        await f.Service.Refresh(user, "https://avatar.test/image", path => { current = path; return Task.CompletedTask; });
        Assert.NotNull(current);
        var original = await File.ReadAllBytesAsync(current);
        var originalPath = current;
        f.Http.Respond = _ => failure == "download" ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Image(1, "\"v2\"");
        if (failure == "decode") f.Decoder.Setup(d => d.GetImageSize(It.IsAny<string>())).Throws(new IOException());
        if (failure == "write") f.Images.Setup(i => i.SaveImage(It.IsAny<Stream>(), "image/png", It.IsAny<string>()))
            .Returns((Stream input, string mime, string path) => { File.WriteAllText(path, "partial"); throw new IOException(); });
        await f.Service.Refresh(user, "https://avatar.test/image", _ => throw new IOException("Commit failed"));
        Assert.Equal(originalPath, current);
        Assert.Equal(original, await File.ReadAllBytesAsync(current));
        Assert.Single(System.IO.Directory.GetFiles(Path.GetDirectoryName(current)!));
        var checkedValidator = false;
        f.Http.Respond = request =>
        {
            Assert.Equal("\"v1\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            checkedValidator = true;
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        };
        await f.Service.Refresh(user, "https://avatar.test/image", _ => throw new InvalidOperationException("Unchanged"));
        Assert.True(checkedValidator);
    }

    [Fact]
    public async Task SuccessfulReplacementPublishesNewFileBeforeRemovingOldOwnedFiles()
    {
        using var f = new Fixture();
        var user = Guid.NewGuid();
        string? previous = null;
        f.Http.Respond = _ => Image();
        await f.Service.Refresh(user, "https://avatar.test/image", path => { previous = path; return Task.CompletedTask; });
        Assert.NotNull(previous);
        var manual = Path.Combine(Path.GetDirectoryName(previous)!, "profile.png");
        await File.WriteAllTextAsync(manual, "preserve manual image");
        f.Http.Respond = _ => Image(1, "\"v2\"");
        string? replacement = null;
        await f.Service.Refresh(user, "https://avatar.test/image", path =>
        {
            Assert.NotEqual(previous, path);
            Assert.True(File.Exists(previous));
            Assert.Equal(1, File.ReadAllBytes(path)[^1]);
            replacement = path;
            return Task.CompletedTask;
        });
        Assert.NotNull(replacement);
        Assert.True(File.Exists(replacement));
        Assert.False(File.Exists(previous));
        Assert.Equal("preserve manual image", await File.ReadAllTextAsync(manual));
    }

    [Theory]
    [InlineData("profilepng", "png")]
    [InlineData("profilejpg", "jpg")]
    [InlineData("profilejpeg", "jpg")]
    [InlineData("profilewebp", "webp")]
    public async Task LegacyNamesRecoverWithoutDownloadingOrOverwritingAnImage(string name, string extension)
    {
        using var f = new Fixture();
        var user = Guid.NewGuid();
        var sourceDirectory = Path.Combine(f.Directory, "old-user-name");
        System.IO.Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, name);
        byte[] bytes = extension switch
        {
            "png" => [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0, 0],
            "jpg" => [255, 216, 255, 224, 0],
            _ => [82, 73, 70, 70, 0, 0, 0, 0, 87, 69, 66, 80, 0],
        };
        await File.WriteAllBytesAsync(source, bytes);
        var targetDirectory = Path.Combine(f.Directory, user.ToString("N"));
        System.IO.Directory.CreateDirectory(targetDirectory);
        var existing = Path.Combine(targetDirectory, "profile." + extension);
        await File.WriteAllTextAsync(existing, "preserve-existing-file");
        string? repaired = null;
        f.Decoder.Setup(d => d.GetImageSize(It.IsAny<string>())).Returns((string path) =>
        {
            Assert.Equal("." + extension, Path.GetExtension(path));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            return new ImageDimensions(1, 1);
        });
        await f.Service.RepairLegacy(user, source, path => { repaired = path; return Task.FromResult(true); });
        Assert.NotNull(repaired);
        Assert.Equal(targetDirectory, Path.GetDirectoryName(repaired));
        Assert.Equal("." + extension, Path.GetExtension(repaired));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(repaired));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        Assert.Equal("preserve-existing-file", await File.ReadAllTextAsync(existing));
        Assert.Empty(f.Http.Requests);
        await f.Service.RepairLegacy(user, repaired, _ => throw new InvalidOperationException("Already repaired"));
        Assert.Single(System.IO.Directory.GetFiles(targetDirectory, "profile-recovered-*"));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("size")]
    [InlineData("decode")]
    [InlineData("dimensions")]
    [InlineData("missing")]
    [InlineData("commit")]
    [InlineData("changed")]
    public async Task FailedRepairKeepsOriginalAndCleansUpUncommittedCopies(string failure)
    {
        using var f = new Fixture();
        var directory = Path.Combine(f.Directory, "user");
        System.IO.Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "profilepng");
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0, 0];
        if (failure == "bytes") bytes = [1, 2, 3];
        if (failure == "size") bytes = new byte[2 * 1024 * 1024 + 1];
        if (failure != "missing") await File.WriteAllBytesAsync(source, bytes);
        if (failure == "decode") f.Decoder.Setup(d => d.GetImageSize(It.IsAny<string>())).Throws(new InvalidOperationException("Undecodable"));
        if (failure == "dimensions") f.Decoder.Setup(d => d.GetImageSize(It.IsAny<string>())).Returns(new ImageDimensions(4097, 1));
        var called = false;
        var user = Guid.NewGuid();
        await f.Service.RepairLegacy(user, source, _ =>
        {
            called = true;
            if (failure == "commit") throw new IOException("Synthetic persistence failure");
            Assert.Equal("changed", failure);
            return Task.FromResult(false);
        });
        Assert.Equal(failure is "commit" or "changed", called);
        Assert.Empty(System.IO.Directory.GetFiles(f.Directory, "profile-recovered-*", SearchOption.AllDirectories));
        if (failure != "missing") Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        // The same account can still obtain a fresh provider image after failed repair.
        var refreshed = false;
        f.Decoder.Setup(d => d.GetImageSize(It.IsAny<string>())).Returns(new ImageDimensions(1, 1));
        await f.Service.Refresh(user, "https://avatar.test/replacement", _ => { refreshed = true; return Task.CompletedTask; });
        Assert.True(refreshed);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("file-link")]
    [InlineData("directory-link")]
    [InlineData("target-link")]
    public async Task RepairRejectsPathsOutsideUserStorageAndSymlinks(string kind)
    {
        using var f = new Fixture();
        var outside = System.IO.Directory.CreateTempSubdirectory("sso-avatar-outside-");
        try
        {
            var user = Guid.NewGuid();
            System.IO.Directory.CreateDirectory(f.Directory);
            var source = Path.Combine(outside.FullName, "profilepng");
            byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0, 0];
            await File.WriteAllBytesAsync(source, bytes);
            var userDirectory = Path.Combine(f.Directory, "user");
            if (kind == "directory-link")
            {
                System.IO.Directory.CreateSymbolicLink(userDirectory, outside.FullName);
                source = Path.Combine(userDirectory, "profilepng");
            }
            else if (kind is "file-link" or "target-link")
            {
                System.IO.Directory.CreateDirectory(userDirectory);
                var local = Path.Combine(userDirectory, "profilepng");
                if (kind == "file-link") File.CreateSymbolicLink(local, source);
                else
                {
                    File.Copy(source, local);
                    System.IO.Directory.CreateSymbolicLink(Path.Combine(f.Directory, user.ToString("N")), outside.FullName);
                }
                source = local;
            }
            await f.Service.RepairLegacy(user, source, _ => throw new InvalidOperationException("Must not commit"));
            f.Decoder.Verify(d => d.GetImageSize(It.IsAny<string>()), Times.Never);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(outside.FullName, "profilepng")));
            Assert.Single(System.IO.Directory.GetFiles(outside.FullName));
        }
        finally
        {
            outside.Delete(true);
        }
    }

    [Fact]
    public async Task RecoveryAndRefreshShareTheSamePerUserLock()
    {
        using var f = new Fixture();
        var user = Guid.NewGuid();
        var directory = Path.Combine(f.Directory, "user");
        System.IO.Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "profilepng");
        await File.WriteAllBytesAsync(source, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0, 0 });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repair = f.Service.RepairLegacy(user, source, async _ => { entered.SetResult(); await release.Task; return true; });
        Task refresh = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            refresh = f.Service.Refresh(user, "https://avatar.test/after-repair", _ => Task.CompletedTask);
            await f.Service.Refresh(Guid.NewGuid(), "https://avatar.test/other", _ => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain("/after-repair", f.Http.Requests);
            Assert.False(refresh.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(repair, refresh).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void PooledAvatarHandlerKeepsNetworkRestrictionsAndDoesNotShareCookies()
    {
        using var handler = AvatarService.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
    }
}

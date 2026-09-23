using System.Collections.Concurrent;
using System.Net;
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
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("sso-avatar", name);
            return new(this, disposeHandler: false);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/failure") throw new HttpRequestException("Synthetic failure");
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

        public Fixture()
        {
            var decoder = new Mock<IImageEncoder>();
            decoder.Setup(d => d.GetImageSize(It.IsAny<string>())).Returns(new ImageDimensions(1, 1));
            var images = new Mock<IProviderManager>();
            images.Setup(i => i.SaveImage(It.IsAny<Stream>(), "image/png", It.IsAny<string>())).Returns(Task.CompletedTask);
            var server = new Mock<IServerConfigurationManager>();
            server.Setup(s => s.ApplicationPaths.UserConfigurationDirectoryPath).Returns(Directory);
            Service = new(decoder.Object, images.Object, server.Object, Http, NullLogger<AvatarService>.Instance);
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

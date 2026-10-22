using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchPlexTuner.Models;
using TwitchPlexTuner.Services;

namespace TwitchPlexTuner.Tests;

public class JellyfinServiceTests
{
    [Fact]
    public async Task RefreshGuideAsync_UsesMediaBrowserAuthorizationForBothRequests()
    {
        const string apiKey = "test-api-key";
        var handler = new RecordingHttpMessageHandler();
        var factory = new TestHttpClientFactory(handler);
        var service = new JellyfinService(
            factory,
            Options.Create(new TwitchConfig
            {
                JellyfinUrl = "http://jellyfin.test",
                JellyfinApiKey = apiKey
            }),
            NullLogger<JellyfinService>.Instance);

        await service.RefreshGuideAsync();

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/ScheduledTasks", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("/ScheduledTasks/Running/task-id", handler.Requests[1].RequestUri!.AbsolutePath);

        foreach (var request in handler.Requests)
        {
            Assert.NotNull(request.Authorization);
            Assert.Equal("MediaBrowser", request.Authorization!.Scheme);
            Assert.Contains($"Token=\"{apiKey}\"", request.Authorization.Parameter);
            Assert.False(request.HasLegacyToken);
        }
    }

    [Fact]
    public async Task RefreshGuideAsync_RetriesTransientTransportFailure()
    {
        var handler = new RecordingHttpMessageHandler { FailFirstRequest = true };
        var service = new JellyfinService(
            new TestHttpClientFactory(handler),
            Options.Create(new TwitchConfig
            {
                JellyfinUrl = "http://jellyfin.test",
                JellyfinApiKey = "test-api-key"
            }),
            NullLogger<JellyfinService>.Instance);

        await service.RefreshGuideAsync();

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = new();
        public bool FailFirstRequest { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization,
                request.Headers.Contains("X-Emby-Token")));

            if (FailFirstRequest && Requests.Count == 1)
                throw new IOException("Simulated premature response end.");

            var response = request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """[{"Key":"RefreshGuide","Name":"Refresh Guide","Id":"task-id"}]""")
                }
                : new HttpResponseMessage(HttpStatusCode.NoContent);

            return Task.FromResult(response);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        AuthenticationHeaderValue? Authorization,
        bool HasLegacyToken);
}

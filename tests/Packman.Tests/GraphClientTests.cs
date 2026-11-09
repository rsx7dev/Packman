using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>GraphClient's throttling and transient retries, and the uncertain-mutation paths for POST.</summary>
public sealed class GraphClientTests : GraphStaticsFixture
{
    private const string Url = GraphClient.MobileApps;

    private static readonly GraphClient Graph = new(() => Task.FromResult("test-token"));

    private static RecordingHandler Answer(Func<int, HttpResponseMessage> respond)
    {
        var handler = new RecordingHandler((_, call) => respond(call));
        UseHandler(handler);
        return handler;
    }

    private static HttpResponseMessage Throttled()
    {
        var response = Fakes.Json(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        return response;
    }

    [Fact]
    public async Task Get429WithRetryAfterIsRetried()
    {
        var handler = Answer(call => call == 1 ? Throttled() : Fakes.Json(HttpStatusCode.OK));

        var response = await Graph.GetAsync(Url, "List apps");

        Assert.Equal(2, handler.Calls);
        Assert.True(response.IsSuccess);
        Assert.False(response.HadUncertainAttempt);
    }

    [Fact]
    public async Task Post429IsRetriedOnce()
    {
        var handler = Answer(call => call == 1 ? Throttled() : Fakes.Json(HttpStatusCode.Created, "{\"id\":\"a\"}"));

        var response = await Graph.PostAsync(Url, new { displayName = "x" }, "Create app");

        Assert.Equal(2, handler.Calls);
        Assert.True(response.IsSuccess);
        Assert.False(response.HadUncertainAttempt);
    }

    [Fact]
    public async Task Get503ThenSuccessIsMarkedUncertain()
    {
        var handler = Answer(call => Fakes.Json(call == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        var response = await Graph.GetAsync(Url, "List apps");

        Assert.Equal(2, handler.Calls);
        Assert.True(response.IsSuccess);
        Assert.True(response.HadUncertainAttempt);
    }

    [Fact]
    public async Task Get503EveryTimeStopsAfterFiveAttempts()
    {
        var handler = Answer(_ => Fakes.Json(HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<GraphException>(() => Graph.GetAsync(Url, "List apps"));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task Patch504IsRetried()
    {
        var handler = Answer(call => Fakes.Json(call == 1 ? HttpStatusCode.GatewayTimeout : HttpStatusCode.NoContent, ""));

        var response = await Graph.PatchAsync($"{Url}/app-1", new { displayName = "x" }, "Update app");

        Assert.Equal(2, handler.Calls);
        Assert.True(response.IsSuccess);
        Assert.All(handler.Requests, r => Assert.Equal("PATCH", r.Method));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task GetTransientServerErrorIsRetried(HttpStatusCode transient)
    {
        var handler = Answer(call => Fakes.Json(call == 1 ? transient : HttpStatusCode.OK));

        var response = await Graph.GetAsync(Url, "Poll upload state");

        Assert.Equal(2, handler.Calls);
        Assert.True(response.IsSuccess);
    }

    [Fact]
    public async Task ReadOnlyPostIsRetriedLikeAGet()
    {
        var handler = Answer(call => Fakes.Json(call == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        var response = await Graph.PostQueryAsync(Url, new { filter = "x" }, "Status report");

        Assert.Equal(2, handler.Calls);
        Assert.True(response.IsSuccess);
        Assert.All(handler.Requests, r => Assert.Equal("POST", r.Method));
    }

    [Fact]
    public async Task ReadOnlyPostThatKeepsFailingIsAnOrdinaryError()
    {
        var handler = Answer(_ => Fakes.Json(HttpStatusCode.ServiceUnavailable));

        var response = await Graph.PostQueryAsync(Url, null, "Status report", throwOnError: false);

        Assert.False(response.IsSuccess);
        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task Post503IsUncertainAndNotRetried()
    {
        var handler = Answer(_ => Fakes.Json(HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<GraphMutationUncertainException>(
            () => Graph.PostAsync(Url, new { displayName = "x" }, "Create app"));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("Create app", ex.Operation);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Post503IsUncertainEvenWithoutThrowOnError()
    {
        var handler = Answer(_ => Fakes.Json(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<GraphMutationUncertainException>(
            () => Graph.PostAsync(Url, null, "Create app", throwOnError: false));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PostNetworkFailureIsUncertainAndNotRetried()
    {
        var failure = new HttpRequestException("connection reset");
        var handler = Answer(_ => throw failure);

        var ex = await Assert.ThrowsAsync<GraphMutationUncertainException>(
            () => Graph.PostAsync(Url, new { displayName = "x" }, "Create app"));

        Assert.Same(failure, ex.InnerException);
        Assert.Null(ex.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetNetworkFailureIsRethrownAfterFiveAttempts()
    {
        var handler = Answer(_ => throw new HttpRequestException("connection reset"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Graph.GetAsync(Url, "List apps"));

        Assert.Equal(5, handler.Calls);
    }
}

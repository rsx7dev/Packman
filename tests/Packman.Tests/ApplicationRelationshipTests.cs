using System.Net;
using System.Net.Http;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

public sealed class ApplicationRelationshipTests : GraphStaticsFixture
{
    private const string AppUrl = GraphClient.MobileApps + "/app-1";
    private readonly IntuneService _apps = new(() => Task.FromResult("test-token"));

    [Fact]
    public async Task DetailReadsRelationshipsFromGraph()
    {
        var handler = AnswerRelationships(_ => Fakes.Json(HttpStatusCode.OK, "{\"value\":[]}"));

        await _apps.GetApplicationDetailAsync("app-1");

        Assert.Contains(handler.Requests, r => r == ("GET", AppUrl + "/relationships"));
    }

    private static RecordingHandler AnswerRelationships(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new RecordingHandler((request, _) => request.RequestUri!.AbsoluteUri switch
        {
            AppUrl => Fakes.Json(HttpStatusCode.OK, """{"id":"app-1","displayName":"Current app","publishingState":"published"}"""),
            AppUrl + "/assignments" => Fakes.Json(HttpStatusCode.OK, "{\"value\":[]}"),
            GraphClient.Beta + "/deviceManagement/reports/getAppStatusOverviewReport" => Fakes.Json(HttpStatusCode.OK),
            var url when url.StartsWith(AppUrl + "/relationships") => respond(request),
            _ => throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}"),
        });
        UseHandler(handler);
        return handler;
    }
}

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

    [Fact]
    public async Task DetailSeparatesDependenciesAndBothSupersedenceDirectionsAcrossPages()
    {
        var handler = AnswerRelationships(request => Fakes.Json(HttpStatusCode.OK,
            request.RequestUri!.Query.Length == 0 ? """
            {
              "value": [
                {"@odata.type":"#microsoft.graph.mobileAppDependency","sourceId":"app-1","targetId":"runtime","targetDisplayName":"Runtime","targetDisplayVersion":"8.0","targetType":"child","dependencyType":"autoInstall"},
                {"@odata.type":"#microsoft.graph.mobileAppDependency","sourceId":"app-1","targetId":"dependent","targetDisplayName":"Uses this app","targetType":"parent","dependencyType":"detect"},
                {"@odata.type":"#microsoft.graph.mobileAppSupersedence","sourceId":"app-1","targetId":"old","targetDisplayName":"Older app","targetDisplayVersion":"1.0","targetType":"child","supersedenceType":"update"}
              ],
              "@odata.nextLink":"https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/app-1/relationships?$skiptoken=next"
            }
            """ : """
            {
              "value": [
                {"@odata.type":"#microsoft.graph.mobileAppSupersedence","sourceId":"app-1","targetId":"new","targetDisplayName":"Newer app","targetDisplayVersion":"3.0","targetType":"parent","supersedenceType":"replace"},
                {"@odata.type":"#microsoft.graph.mobileAppDependency","sourceId":"app-1","targetId":"detect","targetDisplayName":null,"targetDisplayVersion":null,"targetType":"child","dependencyType":"detect"},
                {"@odata.type":"#microsoft.graph.mobileAppSupersedence","targetId":"future","targetType":"unknownFutureValue"},
                {"@odata.type":"#microsoft.graph.mobileAppRelationship","targetId":"other","targetType":"child"}
              ]
            }
            """));

        var detail = await _apps.GetApplicationDetailAsync("app-1");

        var relationships = Assert.IsType<Models.ApplicationRelationships>(detail.Relationships);
        Assert.Equal("", relationships.Error);
        Assert.Collection(relationships.Dependencies,
            app => Assert.Equal(("runtime", "Runtime", "8.0"), (app.Id, app.DisplayName, app.Version)),
            app => Assert.Equal(("detect", "detect", ""), (app.Id, app.DisplayName, app.Version)));
        var older = Assert.Single(relationships.Supersedes);
        Assert.Equal(("old", "Older app", "1.0"), (older.Id, older.DisplayName, older.Version));
        var newer = Assert.Single(relationships.SupersededBy);
        Assert.Equal(("new", "Newer app", "3.0"), (newer.Id, newer.DisplayName, newer.Version));
        Assert.All(handler.Requests.Where(r => r.Url.Contains("/relationships")), r => Assert.Equal("GET", r.Method));
    }

    [Fact]
    public async Task EmptyRelationshipsAreASuccessfulRead()
    {
        AnswerRelationships(_ => Fakes.Json(HttpStatusCode.OK, "{\"value\":[]}"));

        var detail = await _apps.GetApplicationDetailAsync("app-1");

        var relationships = Assert.IsType<Models.ApplicationRelationships>(detail.Relationships);
        Assert.Equal("", relationships.Error);
        Assert.Empty(relationships.Dependencies);
        Assert.Empty(relationships.Supersedes);
        Assert.Empty(relationships.SupersededBy);
    }

    [Fact]
    public async Task RelationshipFailurePreservesDetailsWithoutReportingAnEmptySuccess()
    {
        AnswerRelationships(_ => Fakes.Json(HttpStatusCode.Forbidden,
            """{"error":{"code":"Forbidden","message":"Access denied"}}"""));

        var detail = await _apps.GetApplicationDetailAsync("app-1");

        Assert.Equal("Current app", detail.DisplayName);
        var relationships = Assert.IsType<Models.ApplicationRelationships>(detail.Relationships);
        Assert.Contains("403", relationships.Error);
        Assert.Contains("Access denied", relationships.Error);
    }

    [Fact]
    public async Task FailedLaterPageDoesNotShowPartialRelationshipsAsComplete()
    {
        AnswerRelationships(request => request.RequestUri!.Query.Length == 0
            ? Fakes.Json(HttpStatusCode.OK, """
                {"value":[{"@odata.type":"#microsoft.graph.mobileAppDependency","targetId":"runtime","targetType":"child"}],
                 "@odata.nextLink":"https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/app-1/relationships?$skiptoken=next"}
                """)
            : Fakes.Json(HttpStatusCode.Forbidden));

        var detail = await _apps.GetApplicationDetailAsync("app-1");

        var relationships = Assert.IsType<Models.ApplicationRelationships>(detail.Relationships);
        Assert.NotEmpty(relationships.Error);
        Assert.Empty(relationships.Dependencies);
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

using System.Net;
using Shouldly;
using Squeezr.Semantic.Embeddings;

namespace Squeezr.Tests.Embeddings;

public class LegacyCollectionReaperTests
{
    private const string LegacyEndpoint = "http://localhost:6333";

    [Fact]
    public async Task DropsOnlyAllowedCollections_AndNeverTouchesForeignNames()
    {
        var handler = new FakeHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/collections")
            {
                return Json("""{"result":{"collections":[{"collection_name":"squeezr-d23ab1c2"},{"collection_name":"grimoire-ae74daf9"},{"collection_name":"system"}]}}""");
            }

            return Json("""{"result":true}""");
        });

        var reap = await LegacyCollectionReaper.DropAsync(
            LegacyEndpoint, ["squeezr-d23ab1c2"], CancellationToken.None, handler);

        reap.Ran.ShouldBeTrue();
        reap.Dropped.ShouldBe(["squeezr-d23ab1c2"]);
        handler.Calls.ShouldNotContain(c => c.Uri.Contains("grimoire"));
        handler.Calls.ShouldNotContain(c => c.Uri.Contains("system"));
        handler.Calls.Count(c => c.Method == "DELETE").ShouldBe(1);
    }

    [Fact]
    public async Task NoMatchingCollections_NoDeleteCalls()
    {
        var handler = new FakeHandler(request =>
            Json("""{"result":{"collections":[{"collection_name":"grimoire-ae74daf9"}]}}"""));

        var reap = await LegacyCollectionReaper.DropAsync(
            LegacyEndpoint, ["squeezr-d23ab1c2"], CancellationToken.None, handler);

        reap.Ran.ShouldBeTrue();
        reap.Dropped.ShouldBeEmpty();
        handler.Calls.ShouldNotContain(c => c.Method == "DELETE");
    }

    [Fact]
    public async Task UnreachableLegacyEndpoint_SkipsWithReason()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("down"));

        var reap = await LegacyCollectionReaper.DropAsync(
            LegacyEndpoint, ["squeezr-d23ab1c2"], CancellationToken.None, handler);

        reap.Ran.ShouldBeFalse();
        reap.Dropped.ShouldBeEmpty();
        reap.SkipReason.ShouldContain("unreachable");
    }

    [Fact]
    public async Task DeleteFailure_MovesCollectionToKept()
    {
        var handler = new FakeHandler(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            }

            return Json("""{"result":{"collections":[{"collection_name":"squeezr-d23ab1c2"}]}}""");
        });

        var reap = await LegacyCollectionReaper.DropAsync(
            LegacyEndpoint, ["squeezr-d23ab1c2"], CancellationToken.None, handler);

        reap.Dropped.ShouldBeEmpty();
        reap.Kept.ShouldBe(["squeezr-d23ab1c2"]);
    }

    private static HttpResponseMessage Json(string content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json"),
        };
        return response;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Method, string Uri)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method.Method, request.RequestUri!.ToString()));
            return Task.FromResult(respond(request));
        }
    }
}

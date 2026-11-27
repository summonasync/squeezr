using System.Net;
using System.Text;
using Shouldly;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Embeddings;

public class OllamaModelEnsurerTests
{
    private static OllamaOptions Options => new()
    {
        Endpoint = "http://localhost:11434",
        Model = "nomic-embed-text"
    };

    [Fact]
    public void Ensure_ModelMissing_PullsModelAndDrainsResponseStream()
    {
        var pullContent = new DrainTrackingContent("{\"status\":\"pulling\"}\n{\"status\":\"success\"}\n");
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"models":[]}"""),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = pullContent });

        OllamaModelEnsurer.Ensure(Options, handler);

        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].Method.ShouldBe("GET");
        handler.Requests[0].Path.ShouldBe("/api/tags");
        handler.Requests[1].Method.ShouldBe("POST");
        handler.Requests[1].Path.ShouldBe("/api/pull");
        handler.Requests[1].Body.ShouldNotBeNull().ShouldContain("\"name\":\"nomic-embed-text\"");
        pullContent.WasRead.ShouldBeTrue();
    }

    [Fact]
    public void Ensure_ModelPresent_SkipsPull()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"models":[{"name":"nomic-embed-text:latest","size":2741270220}]}"""));

        OllamaModelEnsurer.Ensure(Options, handler);

        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Path.ShouldBe("/api/tags");
    }

    [Fact]
    public void Ensure_ServerUnreachable_ThrowsWorkspaceExceptionWithServicesUpHint()
    {
        var handler = new StubHttpMessageHandler(new HttpRequestException("connection refused"));

        var error = Should.Throw<WorkspaceException>(() => OllamaModelEnsurer.Ensure(Options, handler));

        error.Message.ShouldContain("Ollama is not reachable at http://localhost:11434");
        error.Message.ShouldContain("squeezr services up");
    }

    [Fact]
    public void Ensure_PullFails_ThrowsWorkspaceExceptionWithModelName()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"models":[]}"""),
            StubHttpMessageHandler.Json(HttpStatusCode.InternalServerError, """{"error":"registry down"}"""));

        var error = Should.Throw<WorkspaceException>(() => OllamaModelEnsurer.Ensure(Options, handler));

        error.Message.ShouldContain("Failed to pull ollama model 'nomic-embed-text'");
    }

    [Fact]
    public void Ensure_TagsErrorStatus_ReportsStatusNotUnreachable()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.BadGateway, """{"error":"proxy down"}"""));

        var error = Should.Throw<WorkspaceException>(() => OllamaModelEnsurer.Ensure(Options, handler));

        error.Message.ShouldContain("returned status 502");
        error.Message.ShouldNotContain("not reachable");
    }

    [Theory]
    [MemberData(nameof(PullNetworkFailures))]
    public void Ensure_PullNetworkFailure_ThrowsWithServicesUpHint(Exception pullFailure)
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"models":[]}"""),
            pullFailure);

        var error = Should.Throw<WorkspaceException>(() => OllamaModelEnsurer.Ensure(Options, handler));

        error.Message.ShouldContain("squeezr services up");
        error.Message.ShouldContain("nomic-embed-text");
    }

    public static TheoryData<Exception> PullNetworkFailures =>
    [
        new HttpRequestException("connection reset"),
        new TaskCanceledException("the request timed out"),
    ];

    private sealed class DrainTrackingContent : HttpContent
    {
        private readonly byte[] _bytes;

        public DrainTrackingContent(string content)
            => _bytes = Encoding.UTF8.GetBytes(content);

        public bool WasRead { get; private set; }

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            WasRead = true;
            stream.Write(_bytes, 0, _bytes.Length);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            return stream.WriteAsync(_bytes, 0, _bytes.Length, CancellationToken.None);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }
    }
}

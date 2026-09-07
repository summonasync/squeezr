using System.Net;

namespace Squeezr.Tests.Embeddings;

public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<object> _responses;

    public List<(string Method, string? Path, string? Body)> Requests { get; } = [];

    public StubHttpMessageHandler(params object[] responses)
        => _responses = new Queue<object>(responses);

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : request.Content.ReadAsStringAsync(cancellationToken).Result;
        Requests.Add((request.Method.Method, request.RequestUri?.PathAndQuery, body));

        if (_responses.Count == 0)
            throw new InvalidOperationException("No stubbed response left.");

        var next = _responses.Dequeue();
        if (next is Exception error)
            throw error;
        return Task.FromResult((HttpResponseMessage)next);
    }
}

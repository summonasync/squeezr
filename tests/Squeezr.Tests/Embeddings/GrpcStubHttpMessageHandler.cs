using System.Buffers.Binary;
using System.Net;
using Google.Protobuf;

namespace Squeezr.Tests.Embeddings;

public sealed class GrpcStubHttpMessageHandler : HttpMessageHandler
{
    private readonly IReadOnlyDictionary<string, IMessage> _responses;

    public GrpcStubHttpMessageHandler(IReadOnlyDictionary<string, IMessage> responses)
        => _responses = responses;

    public List<(string Path, byte[] Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add((path, body));

        var payload = _responses.TryGetValue(path, out var message) ? message.ToByteArray() : [];
        var frame = new byte[payload.Length + 5];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length);
        payload.CopyTo(frame, 5);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(frame),
            Version = HttpVersion.Version20,
        };
        response.Content.Headers.TryAddWithoutValidation("content-type", "application/grpc");
        response.TrailingHeaders.TryAddWithoutValidation("grpc-status", "0");
        return response;
    }
}

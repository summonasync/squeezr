using System.Buffers.Binary;
using System.Net;
using Google.Protobuf;
using Grpc.Core;
using Qdrant.Client.Grpc;

namespace Squeezr.Tests.Embeddings;

// A qdrant that holds points and answers on the real wire: the copy is measured against a server that
// counts what it was given, so a lost batch shows up as a number that disagrees instead of passing a stub
// that echoes whatever the test asked for. An unknown path is a test failure rather than an empty answer —
// a copy that reached for a call this product does not make is exactly what this fake exists to notice.
internal sealed class QdrantFakeServer : HttpMessageHandler
{
    // Payload values are kept as the server got them. Deciding Integer or String again on the way out would
    // let the fixture manufacture a defect the real server does not have: an all-digit chunk hash would come
    // back as a number and the copy would read it as an empty hash.
    internal sealed record StoredPoint(ulong Id, float[] Vector, IReadOnlyDictionary<string, Value> Payload);

    private readonly Dictionary<string, Dictionary<ulong, StoredPoint>> _collections = [];
    private readonly Dictionary<string, ulong> _sizes = [];
    private readonly List<string> _paths = [];
    private readonly List<int> _upsertBatchSizes = [];
    private List<(string Collection, StoredPoint[] Points)> _deferred = [];
    private int _deferredCountReads;
    private int _countReads;

    // The batch a server acknowledges and then loses, numbered from one.
    internal int? SilentlyDroppedUpsertBatch { get; set; }

    // The batch a server acknowledges and applies only on the second later count — a peer answering a read
    // before the write landed. The first count after the stash is short, the second one is whole.
    internal int? LaggingUpsertBatch { get; set; }

    // Every call comes back as an unavailable transport, paths still recorded: the state a session start
    // sees when the infra is down, and the proof that nothing was written on the way to reporting it.
    internal bool Unavailable { get; set; }

    // Transport death for one path only, after the calls before it answered normally: the state of a server
    // that greeted the availability probe and died on the next question.
    internal string? DiesOnPath { get; set; }

    // Transport death on the Nth count read, counting from one, whichever collection it names.
    internal int? DiesOnCountRead { get; set; }

    // Points the parent adds (positive) or drops (negative) while a copy walks it. The change lands after
    // the page that answers is built, so it never shows up in the walk that caused it — which is exactly how
    // a live parent's own index pass looks to a scroll.
    internal int SourceMovesByOnScroll { get; set; }

    internal IReadOnlyList<string> Paths => _paths;
    internal IReadOnlyList<int> UpsertBatchSizes => _upsertBatchSizes;
    internal int ScrollCallCount => _paths.Count(path => path == "/qdrant.Points/Scroll");
    internal int CountCallCount => _paths.Count(path => path == "/qdrant.Points/Count");

    internal void Seed(string collection, ulong dimensions, IEnumerable<StoredPoint> points)
    {
        _sizes[collection] = dimensions;
        _collections[collection] = points.ToDictionary(point => point.Id);
    }

    internal bool Holds(string collection) => _collections.ContainsKey(collection);

    internal IReadOnlyList<string> CollectionNames() => _collections.Keys.Order(StringComparer.Ordinal).ToList();

    internal IReadOnlyList<StoredPoint> Points(string collection) =>
        Required(collection).Values.OrderBy(point => point.Id).ToList();

    internal static StoredPoint Point(
        ulong id,
        string chunkHash,
        float[] vector,
        string kind = "code",
        uint fileId = 17,
        uint repoId = 3,
        int startLine = 1,
        int endLine = 9)
    {
        var payload = new Dictionary<string, Value>
        {
            ["chunkHash"] = new Value { StringValue = chunkHash },
            ["fileId"] = new Value { IntegerValue = fileId },
            ["repoId"] = new Value { IntegerValue = repoId },
            ["startLine"] = new Value { IntegerValue = startLine },
            ["endLine"] = new Value { IntegerValue = endLine },
        };
        if (kind.Length > 0)
        {
            payload["kind"] = new Value { StringValue = kind };
        }

        return new StoredPoint(id, vector, payload);
    }

    // Payload numbers stay inside the long qdrant carries them in: a real fileId is a LiteDB row id, small,
    // and a fixture that invents a 64-bit one would not survive its own round trip.
    internal static StoredPoint PointWithRawPayload(ulong id, float[] vector, params (string Key, Value Value)[] payload) =>
        new(id, vector, payload.ToDictionary(entry => entry.Key, entry => entry.Value));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        _paths.Add(path);
        if (Unavailable || DiesOnPath == path)
        {
            return TransportDown();
        }

        if (path == "/qdrant.Points/Count" && DiesOnCountRead == ++_countReads)
        {
            return TransportDown();
        }

        var payload = body.Length > 5 ? body.AsSpan(5) : ReadOnlySpan<byte>.Empty;

        IMessage response = path switch
        {
            "/qdrant.Collections/List" => new ListCollectionsResponse
            {
                Collections = { _collections.Keys.Select(name => new CollectionDescription { Name = name }) },
            },
            "/qdrant.Collections/CollectionExists" => new CollectionExistsResponse
            {
                Result = new CollectionExists
                {
                    Exists = Holds(CollectionExistsRequest.Parser.ParseFrom(payload).CollectionName),
                },
            },
            "/qdrant.Collections/Create" => Create(CreateCollection.Parser.ParseFrom(payload)),
            "/qdrant.Collections/Get" => Info(GetCollectionInfoRequest.Parser.ParseFrom(payload).CollectionName),
            "/qdrant.Points/Upsert" => Upsert(UpsertPoints.Parser.ParseFrom(payload)),
            "/qdrant.Points/Count" => Count(CountPoints.Parser.ParseFrom(payload)),
            "/qdrant.Points/Scroll" => Scroll(ScrollPoints.Parser.ParseFrom(payload)),
            "/qdrant.Points/CreateFieldIndex" => Ack(),
            _ => throw new NotSupportedException($"the fake qdrant does not answer '{path}'"),
        };

        return Frame(response);
    }

    private IMessage Create(CreateCollection request)
    {
        if (!_collections.TryAdd(request.CollectionName, []))
        {
            throw new RpcException(
                new Status(StatusCode.AlreadyExists, $"collection '{request.CollectionName}' exists"));
        }

        _sizes[request.CollectionName] = request.VectorsConfig.Params.Size;
        return new CollectionOperationResponse { Result = true };
    }

    private IMessage Info(string name)
    {
        var size = _sizes.TryGetValue(name, out var known)
            ? known
            : throw new RpcException(new Status(StatusCode.NotFound, $"collection '{name}' not found"));

        return new GetCollectionInfoResponse
        {
            Result = new CollectionInfo
            {
                Config = new CollectionConfig
                {
                    Params = new CollectionParams
                    {
                        VectorsConfig = new VectorsConfig { Params = new VectorParams { Size = size } },
                    },
                },
            },
        };
    }

    private IMessage Upsert(UpsertPoints request)
    {
        _upsertBatchSizes.Add(request.Points.Count);
        var target = Required(request.CollectionName);

        if (SilentlyDroppedUpsertBatch == _upsertBatchSizes.Count)
        {
            return Ack();
        }

        if (LaggingUpsertBatch == _upsertBatchSizes.Count)
        {
            _deferred.Add((request.CollectionName, [.. request.Points.Select(point => Store(point))]));
            _deferredCountReads = 0;
            return Ack();
        }

        foreach (var point in request.Points)
        {
            target[point.Id.Num] = Store(point);
        }

        return Ack();
    }

    private IMessage Count(CountPoints request)
    {
        // a lagging peer is late for the collection it was written to: counting the parent's reads too would
        // land the replay on the read before the one this pass retries on, and the retry arm would go unpinned
        if (_deferred.Count > 0 && _deferred.Any(entry => entry.Collection == request.CollectionName))
        {
            _deferredCountReads++;
            if (_deferredCountReads >= 2)
            {
                foreach (var (collection, points) in _deferred)
                {
                    var target = Required(collection);
                    foreach (var point in points)
                    {
                        target[point.Id] = point;
                    }
                }

                _deferred = [];
            }
        }

        return new CountResponse
        {
            Result = new CountResult
            {
                Count = (ulong)Required(request.CollectionName).Count,
            },
        };
    }

    private IMessage Scroll(ScrollPoints request)
    {
        var points = Required(request.CollectionName);
        var ordered = points.Keys.Order().ToList();
        var from = request.Offset is { } offset ? ordered.IndexOf(offset.Num) + 1 : 0;
        var taken = ordered.Skip(from).Take((int)request.Limit).ToList();

        var response = new ScrollResponse();
        foreach (var id in taken)
        {
            response.Result.Add(Retrieve(points[id], request));
        }

        if (taken.Count > 0 && from + taken.Count < ordered.Count)
        {
            response.NextPageOffset = new PointId { Num = taken[^1] };
        }

        MoveSource(points, ordered);
        return response;
    }

    // The parent's own index pass, arriving as a page boundary: the walk that already has the page in hand
    // never sees these, the count that follows the walk does.
    private void MoveSource(Dictionary<ulong, StoredPoint> points, List<ulong> orderedBefore)
    {
        if (SourceMovesByOnScroll > 0)
        {
            var top = orderedBefore.Count > 0 ? orderedBefore[^1] : 0UL;
            for (var added = 1; added <= SourceMovesByOnScroll; added++)
            {
                var id = top + (ulong)added;
                points[id] = Point(id, $"grown-{id}", [0.5f, 0.5f]);
            }

            return;
        }

        for (var dropped = 0; dropped < -SourceMovesByOnScroll && orderedBefore.Count > dropped; dropped++)
        {
            points.Remove(orderedBefore[orderedBefore.Count - 1 - dropped]);
        }
    }

    private static RetrievedPoint Retrieve(StoredPoint point, ScrollPoints request)
    {
        var retrieved = new RetrievedPoint { Id = new PointId { Num = point.Id } };

        if (request.WithPayload is { Enable: true })
        {
            foreach (var (key, value) in point.Payload)
            {
                retrieved.Payload[key] = value;
            }
        }

        if (request.WithVectors is { Enable: true })
        {
            retrieved.Vectors = new VectorsOutput
            {
                Vector = new VectorOutput { Dense = new DenseVector { Data = { point.Vector } } },
            };
        }

        return retrieved;
    }

    private static StoredPoint Store(PointStruct point) =>
        new(
            point.Id.Num,
            [.. point.Vectors.Vector.Dense.Data],
            point.Payload.ToDictionary(entry => entry.Key, entry => entry.Value));

    private Dictionary<ulong, StoredPoint> Required(string collection) =>
        _collections.TryGetValue(collection, out var points)
            ? points
            : throw new RpcException(new Status(StatusCode.NotFound, $"collection '{collection}' not found"));

    private static IMessage Ack() =>
        new PointsOperationResponse { Result = new UpdateResult { Status = UpdateStatus.Acknowledged } };

    private static HttpResponseMessage TransportDown()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([]),
            Version = HttpVersion.Version20,
        };
        response.Content.Headers.TryAddWithoutValidation("content-type", "application/grpc");
        response.Headers.TryAddWithoutValidation("grpc-status", ((int)StatusCode.Unavailable).ToString());
        return response;
    }

    private static HttpResponseMessage Frame(IMessage message)
    {
        var body = message.ToByteArray();
        var frame = new byte[body.Length + 5];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), body.Length);
        body.CopyTo(frame.AsSpan(5));

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

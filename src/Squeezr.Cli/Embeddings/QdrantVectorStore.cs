using System.Security.Cryptography;
using Grpc.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Embeddings;

public sealed class QdrantVectorStore : IVectorStore
{
    private const int BatchSize = 100;
    private const uint ScrollPageSize = 500;
    private static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(2);

    private readonly QdrantClient _client;
    private readonly string _collection;

    public QdrantVectorStore(QdrantConfig config, string workspaceRoot)
        : this(CreateClient(config), GetCollectionName(workspaceRoot))
    {
    }

    private static QdrantClient CreateClient(QdrantConfig config)
    {
        var endpointText = config.ResolvedEndpoint;
        Uri endpoint;
        try
        {
            endpoint = new Uri(endpointText);
        }
        catch (UriFormatException ex)
        {
            throw new WorkspaceException($"Invalid Qdrant endpoint '{endpointText}': {ex.Message}");
        }

        return new QdrantClient(endpoint);
    }

    internal QdrantVectorStore(QdrantClient client, string collection)
    {
        _client = client;
        _collection = collection;
    }

    public string Collection => _collection;

    public static string GetCollectionName(string workspaceRoot)
    {
        var hash = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(workspaceRoot));
        return "squeezr-" + Convert.ToHexString(hash).ToLowerInvariant()[..12];
    }

    public static ulong PointIdFromChunkHash(string chunkHash) => ChunkPointId.FromHash(chunkHash);

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AvailabilityTimeout);
        try
        {
            await _client.ListCollectionsAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task EnsureCollectionAsync(int dimensions, CancellationToken ct)
    {
        CollectionInfo? info = null;
        if (!await _client.CollectionExistsAsync(_collection, ct))
        {
            await _client.CreateCollectionAsync(
                _collection,
                new VectorParams { Size = (ulong)dimensions, Distance = Distance.Cosine },
                cancellationToken: ct);
        }
        else
        {
            info = await _client.GetCollectionInfoAsync(_collection, ct);
            var existing = info.Config.Params.VectorsConfig?.Params?.Size;
            if (existing.HasValue && existing.Value != (ulong)dimensions)
            {
                throw new VectorDimensionMismatchException(_collection, existing.Value, dimensions);
            }
        }

        if (info?.PayloadSchema?.ContainsKey("kind") == true)
        {
            return;
        }

        try
        {
            await _client.CreatePayloadIndexAsync(
                _collection,
                "kind",
                PayloadSchemaType.Keyword,
                null,
                cancellationToken: ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
        }
    }

    public async Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
    {
        for (var i = 0; i < points.Count; i += BatchSize)
        {
            var batch = new List<PointStruct>(Math.Min(BatchSize, points.Count - i));
            var end = Math.Min(i + BatchSize, points.Count);
            for (var j = i; j < end; j++)
            {
                batch.Add(ToPointStruct(points[j]));
            }

            await _client.UpsertAsync(_collection, batch, cancellationToken: ct);
        }
    }

    private static PointStruct ToPointStruct(VectorPoint p)
    {
        var point = new PointStruct
        {
            Id = p.Id,
            Vectors = p.Vector,
            Payload =
            {
                ["chunkHash"] = p.ChunkHash,
                ["fileId"] = (long)p.FileId,
                ["repoId"] = (long)p.RepoId,
                ["startLine"] = (long)p.StartLine,
                ["endLine"] = (long)p.EndLine,
            },
        };

        if (p.Kind is { } kind)
        {
            point.Payload["kind"] = kind;
        }

        return point;
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct)
    {
        Filter? qdrantFilter = null;
        if (filter.RepoId.HasValue || filter.Kind is not null)
        {
            qdrantFilter = new Filter();
            if (filter.RepoId.HasValue)
            {
                qdrantFilter.Must.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "repoId",
                        Match = new Match { Integer = (long)filter.RepoId.Value },
                    },
                });
            }

            if (filter.Kind is not null)
            {
                qdrantFilter.Must.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "kind",
                        Match = new Match { Keyword = filter.Kind },
                    },
                });
            }
        }

        var results = await _client.QueryAsync(
            _collection,
            vector,
            filter: qdrantFilter,
            limit: (ulong)topK,
            payloadSelector: new WithPayloadSelector { Enable = true },
            cancellationToken: ct);

        return results.Select(ToHit).ToList();
    }

    public async Task<long> CountAsync(CancellationToken ct)
    {
        try
        {
            var count = await _client.CountAsync(_collection, cancellationToken: ct);
            return (long)count;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return 0;
        }
    }

    // The count that closes a copy's own invariant. Two knobs the plain CountAsync leaves to the server's
    // defaults are load-bearing here: an exact count, because an approximate one is answered out of segment
    // metadata, and a majority read, because the endpoint this product is pointed at is configuration — a
    // replicated Qdrant can answer a follower that has not applied the batch this pass just wrote, and the
    // last page of a long copy is also the biggest. Both were confirmed against the server the developer
    // runs (qdrant 1.19.1): the field is accepted and the count comes back unchanged.
    public async Task<long> CountAfterOwnWritesAsync(CancellationToken ct)
    {
        try
        {
            return (long)await _client.CountAsync(
                _collection,
                exact: true,
                readConsistency: new ReadConsistency { Type = ReadConsistencyType.Majority },
                cancellationToken: ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return 0;
        }
    }

    // The vector size the collection was created with, read off the collection rather than asked of a
    // model: a copy inherits its source's geometry, and a pass that consulted the embedding provider for
    // the number would be reaching for Ollama on a path that must not.
    public async Task<ulong?> GetVectorDimensionsAsync(CancellationToken ct)
    {
        try
        {
            var info = await _client.GetCollectionInfoAsync(_collection, ct);
            return info.Config.Params.VectorsConfig?.Params?.Size;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct)
    {
        var ids = new List<ulong>();
        PointId? offset = null;
        try
        {
            while (true)
            {
                var page = await _client.ScrollAsync(
                    _collection,
                    limit: ScrollPageSize,
                    offset: offset,
                    payloadSelector: new WithPayloadSelector { Enable = false },
                    vectorsSelector: new WithVectorsSelector { Enable = false },
                    cancellationToken: ct);
                if (page.Result.Count == 0)
                {
                    return ids;
                }

                ids.AddRange(page.Result.Select(point => point.Id.Num));
                if (page.NextPageOffset is not { } next)
                {
                    return ids;
                }

                offset = next;
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return ids;
        }
    }

    // One page of a copy: vectors AND payload, in the same pass. ScrollPointIdsAsync cannot serve a copy
    // because it asks for neither, and a copy that read ids here and each point again by id would be N+1
    // round trips re-reading a server that already handed the point over. A point this product did not write
    // cannot be named as one of ours, so it leaves the page through ForeignPoints instead of the caller's
    // exception path: one such point in a parent's collection is a fact about that collection, not a reason
    // to stop a session start.
    public async Task<VectorPointPage> ScrollVectorPointsAsync(ulong? afterPointId, CancellationToken ct)
    {
        try
        {
            var page = await _client.ScrollAsync(
                _collection,
                limit: ScrollPageSize,
                offset: afterPointId is { } id ? new PointId { Num = id } : null,
                payloadSelector: new WithPayloadSelector { Enable = true },
                vectorsSelector: new WithVectorsSelector { Enable = true },
                cancellationToken: ct);

            var points = new List<VectorPoint>(page.Result.Count);
            var foreign = new List<ForeignVectorPoint>();
            foreach (var retrieved in page.Result)
            {
                if (ForeignDefect(retrieved) is { } missing)
                {
                    foreign.Add(new ForeignVectorPoint(retrieved.Id.Num, missing));
                }
                else
                {
                    points.Add(ToOurs(retrieved));
                }
            }

            return new VectorPointPage(
                points,
                page.NextPageOffset is { } next ? next.Num : null,
                foreign);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return VectorPointPage.Empty;
        }
    }

    // What a point this product did not write is missing: the name, not just the verdict, because the report
    // that carries it is the only trace left of a source collection that holds points nobody can account for.
    // The five keys are checked by value kind as well as presence — a point whose chunkHash came back as a
    // number would copy as an empty hash, and the id a later pass derives from that hash would name a
    // different point than the one that landed.
    private static string? ForeignDefect(RetrievedPoint point)
    {
        if (point.Vectors?.Vector?.GetDenseVector() is null)
        {
            return "a dense vector";
        }

        if (!IsText(point.Payload, "chunkHash"))
        {
            return "a text 'chunkHash' payload";
        }

        if (!IsInteger(point.Payload, "fileId"))
        {
            return "an integer 'fileId' payload";
        }

        if (!IsInteger(point.Payload, "repoId"))
        {
            return "an integer 'repoId' payload";
        }

        if (!IsInteger(point.Payload, "startLine"))
        {
            return "an integer 'startLine' payload";
        }

        if (!IsInteger(point.Payload, "endLine"))
        {
            return "an integer 'endLine' payload";
        }

        if (point.Payload.TryGetValue("kind", out var kind) && kind.KindCase != Value.KindOneofCase.StringValue)
        {
            return "a text 'kind' payload";
        }

        return null;
    }

    private static bool IsText(Google.Protobuf.Collections.MapField<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var value) && value.KindCase == Value.KindOneofCase.StringValue;

    private static bool IsInteger(Google.Protobuf.Collections.MapField<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var value) && value.KindCase == Value.KindOneofCase.IntegerValue;

    private static VectorPoint ToOurs(RetrievedPoint point)
    {
        var payload = point.Payload;
        return new VectorPoint(
            point.Id.Num,
            [.. point.Vectors!.Vector!.GetDenseVector()!.Data],
            payload["chunkHash"].StringValue,
            (uint)payload["fileId"].IntegerValue,
            (uint)payload["repoId"].IntegerValue,
            (int)payload["startLine"].IntegerValue,
            (int)payload["endLine"].IntegerValue,
            payload.TryGetValue("kind", out var kind) ? kind.StringValue : null);
    }

    public async Task<IReadOnlyList<uint>> ScrollFileIdsWithoutKindAsync(uint repoId, CancellationToken ct)
    {
        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "repoId",
                        Match = new Match { Integer = (long)repoId },
                    },
                },
                new Condition
                {
                    IsEmpty = new IsEmptyCondition { Key = "kind" },
                },
            },
        };

        var fileIds = new HashSet<uint>();
        PointId? offset = null;
        try
        {
            while (true)
            {
                var page = await _client.ScrollAsync(
                    _collection,
                    filter,
                    limit: ScrollPageSize,
                    offset: offset,
                    payloadSelector: new WithPayloadSelector { Enable = true },
                    vectorsSelector: new WithVectorsSelector { Enable = false },
                    cancellationToken: ct);
                foreach (var point in page.Result)
                {
                    if (point.Payload.TryGetValue("fileId", out var value))
                    {
                        fileIds.Add((uint)value.IntegerValue);
                    }
                }

                if (page.NextPageOffset is not { } next)
                {
                    return fileIds.ToList();
                }

                offset = next;
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return fileIds.ToList();
        }
    }

    public async Task SetKindPayloadsAsync(IReadOnlyDictionary<uint, string> kindByFileId, CancellationToken ct)
    {
        foreach (var group in kindByFileId.GroupBy(kv => kv.Value))
        {
            var fileIds = group.Select(kv => kv.Key).ToList();
            for (var i = 0; i < fileIds.Count; i += BatchSize)
            {
                var batch = fileIds.Skip(i).Take(BatchSize).ToList();
                var filter = new Filter
                {
                    Should =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "fileId",
                                Match = new Match
                                {
                                    Integers = new RepeatedIntegers { Integers = { batch.Select(id => (long)id) } },
                                },
                            },
                        },
                    },
                };

                await _client.SetPayloadAsync(
                    _collection,
                    new Dictionary<string, Value> { ["kind"] = group.Key },
                    filter,
                    cancellationToken: ct);
            }
        }
    }

    public async Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "fileId",
                        Match = new Match { Integers = new RepeatedIntegers { Integers = { fileIds.Select(id => (long)id) } } },
                    },
                },
            },
        };

        try
        {
            await _client.DeleteAsync(_collection, filter, cancellationToken: ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
        }
    }

    public async Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct)
    {
        for (var i = 0; i < pointIds.Count; i += BatchSize)
        {
            var batch = pointIds.Skip(i).Take(BatchSize).ToList();
            try
            {
                await _client.DeleteAsync(_collection, batch, cancellationToken: ct);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                return;
            }
        }
    }

    public async Task DeleteCollectionAsync(CancellationToken ct)
    {
        try
        {
            await _client.DeleteCollectionAsync(_collection, cancellationToken: ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Qdrant collection delete was canceled.", ex);
        }
        catch (RpcException)
        {
        }
        catch (HttpRequestException)
        {
        }
    }

    private static VectorHit ToHit(ScoredPoint point)
    {
        var payload = point.Payload;
        return new VectorHit(
            point.Id.Num,
            payload["chunkHash"].StringValue,
            (uint)payload["fileId"].IntegerValue,
            (uint)payload["repoId"].IntegerValue,
            (int)payload["startLine"].IntegerValue,
            (int)payload["endLine"].IntegerValue,
            point.Score);
    }
}

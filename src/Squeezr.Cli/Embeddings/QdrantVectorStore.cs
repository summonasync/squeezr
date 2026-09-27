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
        Uri endpoint;
        try
        {
            endpoint = new Uri(config.Endpoint);
        }
        catch (UriFormatException ex)
        {
            throw new WorkspaceException($"Invalid Qdrant endpoint '{config.Endpoint}': {ex.Message}");
        }

        return new QdrantClient(endpoint);
    }

    internal QdrantVectorStore(QdrantClient client, string collection)
    {
        _client = client;
        _collection = collection;
    }

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
        if (!await _client.CollectionExistsAsync(_collection, ct))
        {
            await _client.CreateCollectionAsync(
                _collection,
                new VectorParams { Size = (ulong)dimensions, Distance = Distance.Cosine },
                cancellationToken: ct);
            return;
        }

        var info = await _client.GetCollectionInfoAsync(_collection, ct);
        var existing = info.Config.Params.VectorsConfig?.Params?.Size;
        if (existing.HasValue && existing.Value != (ulong)dimensions)
        {
            throw new VectorDimensionMismatchException(_collection, existing.Value, dimensions);
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
                var p = points[j];
                batch.Add(new PointStruct
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
                });
            }

            await _client.UpsertAsync(_collection, batch, cancellationToken: ct);
        }
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, uint? repoId, CancellationToken ct)
    {
        Filter? filter = null;
        if (repoId.HasValue)
        {
            filter = new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "repoId",
                            Match = new Match { Integer = (long)repoId.Value },
                        },
                    },
                },
            };
        }

        var results = await _client.QueryAsync(
            _collection,
            vector,
            filter: filter,
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

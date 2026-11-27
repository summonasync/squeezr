using LiteDB;

namespace Squeezr.Core.Storage;

public sealed class ChunkDocument
{
    [BsonId]
    public long Id { get; set; }

    [BsonField("fileId")]
    public uint FileId { get; set; }

    [BsonField("repoId")]
    public uint RepoId { get; set; }

    [BsonField("symbolId")]
    public long? SymbolId { get; set; }

    [BsonField("startLine")]
    public int StartLine { get; set; }

    [BsonField("endLine")]
    public int EndLine { get; set; }

    [BsonField("text")]
    public string Text { get; set; } = string.Empty;

    [BsonField("tokenCount")]
    public int TokenCount { get; set; }

    [BsonField("chunkHash")]
    public string ChunkHash { get; set; } = string.Empty;

    [BsonField("embeddingStatus")]
    public string VectorStatus { get; set; } = "pending";

    [BsonField("modelKey")]
    public string? ModelKey { get; set; }
}

using LiteDB;

namespace Squeezr.Core.Storage;

public sealed class FileDocument
{
    [BsonId]
    public uint Id { get; set; }

    [BsonField("repoId")]
    public uint RepoId { get; set; }

    [BsonField("path")]
    public string Path { get; set; } = string.Empty;

    [BsonField("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [BsonField("language")]
    public string Language { get; set; } = string.Empty;

    [BsonField("sizeBytes")]
    public long SizeBytes { get; set; }

    [BsonField("lineCount")]
    public int LineCount { get; set; }

    [BsonField("indexedAt")]
    public DateTime IndexedAt { get; set; }
}

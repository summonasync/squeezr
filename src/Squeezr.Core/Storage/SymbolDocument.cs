using LiteDB;

namespace Squeezr.Core.Storage;

public sealed class SymbolDocument
{
    [BsonId]
    public long Id { get; set; }

    [BsonField("fileId")]
    public uint FileId { get; set; }

    [BsonField("repoId")]
    public uint RepoId { get; set; }

    [BsonField("name")]
    public string Name { get; set; } = string.Empty;

    [BsonField("kind")]
    public string Kind { get; set; } = string.Empty;

    [BsonField("signature")]
    public string? Signature { get; set; }

    [BsonField("startLine")]
    public int StartLine { get; set; }

    [BsonField("endLine")]
    public int EndLine { get; set; }

    [BsonField("containerName")]
    public string? ContainerName { get; set; }

    [BsonField("docComment")]
    public string? DocComment { get; set; }
}

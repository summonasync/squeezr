using LiteDB;

namespace Squeezr.Core.Storage;

public sealed class RepoDocument
{
    [BsonId]
    public uint Id { get; set; }

    [BsonField("name")]
    public string Name { get; set; } = string.Empty;

    [BsonField("path")]
    public string Path { get; set; } = string.Empty;

    [BsonField("lastIndexedAt")]
    public DateTime? LastIndexedAt { get; set; }
}

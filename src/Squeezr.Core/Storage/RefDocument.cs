using LiteDB;

namespace Squeezr.Core.Storage;

public sealed class RefDocument
{
    [BsonId]
    public long Id { get; set; }

    [BsonField("fromSymbolId")]
    public long FromSymbolId { get; set; }

    [BsonField("toSymbolId")]
    public string? ToSymbolId { get; set; }

    [BsonField("toName")]
    public string ToName { get; set; } = string.Empty;

    [BsonField("kind")]
    public string Kind { get; set; } = string.Empty;

    [BsonField("fileId")]
    public uint FileId { get; set; }

    [BsonField("line")]
    public int Line { get; set; }
}

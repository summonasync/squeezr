using LiteDB;

namespace Squeezr.Core.Storage;

public sealed class PendingReapDocument
{
    // autoId off: a numeric [BsonId] is treated as an auto-increment sequence, which would silently
    // rewrite the file id on insert — and file id 0 is a real id in every store.
    [BsonId(false)]
    public long FileId { get; set; }

    [BsonField("queuedAt")]
    public DateTime QueuedAt { get; set; }
}

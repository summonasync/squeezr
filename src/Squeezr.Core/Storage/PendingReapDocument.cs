using LiteDB;

namespace Squeezr.Core.Storage;

// One owed point cleanup for a file that died while the vector store could not delete anything.
// The id alone cannot name the dead file: MetaStore.NextId hands out max(_id)+1, so a freed id is
// recycled by the next added file. The entry therefore carries the identity it was queued under and
// replay refuses to delete by an id somebody else now owns.
public sealed class PendingReapDocument
{
    // autoId off: a numeric [BsonId] is treated as an auto-increment sequence, which would silently
    // rewrite the file id on insert — and file id 0 is a real id in every store.
    [BsonId(false)]
    public long FileId { get; set; }

    [BsonField("repoId")]
    public long RepoId { get; set; }

    [BsonField("path")]
    public string? Path { get; set; }

    [BsonField("queuedAt")]
    public DateTime QueuedAt { get; set; }
}

public readonly record struct PendingReapTarget(uint FileId, uint RepoId, string? Path);

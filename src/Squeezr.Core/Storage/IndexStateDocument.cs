using LiteDB;

namespace Squeezr.Core.Storage;

public sealed class IndexStateDocument
{
    [BsonId]
    public string RepoKey { get; set; } = string.Empty;

    [BsonField("cursor")]
    public string? Cursor { get; set; }

    [BsonField("lastFullIndexAt")]
    public DateTime? LastFullIndexAt { get; set; }

    [BsonField("stats")]
    public IndexStateStats Stats { get; set; } = new();
}

public sealed class IndexStateStats
{
    [BsonField("files")]
    public int Files { get; set; }

    [BsonField("symbols")]
    public int Symbols { get; set; }

    [BsonField("chunks")]
    public int Chunks { get; set; }

    [BsonField("ignoredTotal")]
    public int IgnoredTotal { get; set; }

    [BsonField("ignoredBySource")]
    public Dictionary<string, int>? IgnoredBySource { get; set; }

    [BsonField("guardWithheld")]
    public int GuardWithheld { get; set; }

    [BsonField("guardWithheldBuiltIns")]
    public int GuardWithheldBuiltIns { get; set; }

    [BsonField("guardDropped")]
    public int GuardDropped { get; set; }

    [BsonField("trackedButExcluded")]
    public int TrackedButExcluded { get; set; }
}

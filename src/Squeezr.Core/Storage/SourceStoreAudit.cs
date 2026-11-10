namespace Squeezr.Core.Storage;

// What the source store answered with, measured once under its own lock: the numbers the copy verifies
// against, so no second scan re-asks the same question the guard already settled.
public sealed record SourceStoreAudit(int Files, int TotalChunks, int EmbeddedChunks, int ForeignChunks);

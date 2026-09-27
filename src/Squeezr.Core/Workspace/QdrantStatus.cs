namespace Squeezr.Core.Workspace;

public sealed record QdrantStatus(bool Available, long? Points, int PendingChunks, int TotalChunks = 0);

namespace Squeezr.Core.Storage;

public sealed record SemanticCoverage(int TotalChunks, int PendingChunks)
{
    // Floored to 2 decimals so a pending chunk never rounds the share up to 100%.
    public double? Ratio => TotalChunks == 0
        ? null
        : Math.Clamp(Math.Floor((TotalChunks - PendingChunks) * 100.0 / TotalChunks) / 100.0, 0.0, 1.0);
}

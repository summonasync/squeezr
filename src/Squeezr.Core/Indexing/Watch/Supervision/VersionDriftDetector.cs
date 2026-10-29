namespace Squeezr.Core.Indexing.Watch.Supervision;

public static class VersionDriftDetector
{
    public static bool IsDrifted(DateTime processStartUtc, DateTime binaryModifiedUtc) =>
        binaryModifiedUtc > processStartUtc;
}

using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Core.Indexing;
using Squeezr.Semantic.Indexing;

namespace Squeezr.Tests.Cli;

public sealed class VectorsReconcileRenderTests
{
    [Fact]
    public void NeverEmbeddedLine_CountsOnlyTheRowsThatNeverGotAVector()
    {
        var line = VectorsReconcileCommand.NeverEmbeddedLine(Report(chunksWithoutPoints: 9, staleMarkers: 4));

        line.ShouldBe("5 chunk(s) have no point yet — never embedded.");
    }

    [Fact]
    public void NeverEmbeddedLine_Silent_WhenEveryPointlessRowIsAlreadyReportedAsAStaleMarker()
    {
        // the dry run used to print "9 chunk(s) have no point yet — pending embedding" and then, for a
        // subset of those same rows, "marked embedded but have no point" — the first line claimed work was
        // already queued for rows that claim a vector they no longer have
        VectorsReconcileCommand.NeverEmbeddedLine(Report(chunksWithoutPoints: 1, staleMarkers: 1)).ShouldBeNull();
        VectorsReconcileCommand.NeverEmbeddedLine(Report(chunksWithoutPoints: 0, staleMarkers: 0)).ShouldBeNull();
    }

    private static VectorReconcileReport Report(int chunksWithoutPoints, int staleMarkers) =>
        new(
            PointsBefore: 100,
            Chunks: 120,
            LiveHashes: 100,
            Orphans: 0,
            ChunksWithoutPoints: chunksWithoutPoints,
            StaleMarkers: staleMarkers,
            Requeued: staleMarkers,
            Applied: false,
            PointsAfter: null,
            OrphanSample: []);
}

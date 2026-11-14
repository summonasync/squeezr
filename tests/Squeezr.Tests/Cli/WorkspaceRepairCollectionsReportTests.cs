using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Output;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Cli;

// The collection arm's wire is a contract other tools parse, so its shape is pinned here as a shape: which
// keys a reader may ask for, which of them only appear when there is something to say, and which words are
// the stable vocabulary. Under the additive rule a field may be added and none may be removed or reworded,
// and a counted refusal must never collapse into an absent key that reads as a clean sweep.
public sealed class WorkspaceRepairCollectionsReportTests
{
    private const string OwnedEndpoint = "http://localhost:6334";
    private const string ForeignEndpoint = "http://localhost:6333";

    [Fact]
    public void Wire_FullSurvey_NamesEveryKeyTheReaderMayAskFor()
    {
        var report = new WorkspaceRepairCollectionsReport(
            Ran: true,
            Apply: true,
            Endpoints: [OwnedEndpoint, ForeignEndpoint],
            SeenMinted: 2,
            Entries:
            [
                new WorkspaceRepairCollectionEntry(OwnedEndpoint, "squeezr-0a1b2c3d4e5f", "reaped"),
                new WorkspaceRepairCollectionEntry(
                    ForeignEndpoint, "squeezr-1a2b3c4d5e6f", "skipped-not-owned", null, "endpoint-not-owned"),
            ],
            UnreachableEndpoints: [ForeignEndpoint],
            SkipReason: null,
            NotOwnedEndpoints: new[] { ForeignEndpoint },
            EndpointFaults: new[] { new WorkspaceRepairEndpointFault(ForeignEndpoint, "HttpRequestException: refused") });

        var keys = Keys(JsonSerializer.Serialize(report, SqueezrJson.OutputOptions));

        keys.ShouldBe(
            ["apply", "endpointFaults", "endpoints", "entries", "notOwnedEndpoints", "ran", "seenMinted",
                "unreachableEndpoints"],
            "the two counted refusals ride with the reachability half they answer for; skipReason is null here " +
            "and a null writes no key, which is the one silence this wire keeps");
    }

    [Fact]
    public void Wire_TodayShape_StillAnswersTheSameKeysAsBeforeTheGate()
    {
        var report = new WorkspaceRepairCollectionsReport(
            Ran: true,
            Apply: false,
            Endpoints: [OwnedEndpoint],
            SeenMinted: 0,
            Entries: [],
            UnreachableEndpoints: []);

        var keys = Keys(JsonSerializer.Serialize(report, SqueezrJson.OutputOptions));

        keys.ShouldContain("ran");
        keys.ShouldContain("apply");
        keys.ShouldContain("endpoints");
        keys.ShouldContain("seenMinted");
        keys.ShouldContain("entries");
        keys.ShouldContain("unreachableEndpoints",
            "the keys a reader wrote its parser against before this gate are still there with the same meaning");
    }

    [Fact]
    public void Wire_Entry_CarriesTheReasonOnlyWhereTheGateRefused()
    {
        var entries = new WorkspaceRepairCollectionEntry[]
        {
            new(OwnedEndpoint, "squeezr-0a1b2c3d4e5f", WorkspaceCollectionSweep.ActionWouldReap),
            new(OwnedEndpoint, "squeezr-1a2b3c4d5e6f", WorkspaceCollectionSweep.ActionReapFailed, "HTTP 409"),
            new(ForeignEndpoint, "squeezr-2a3b4c5d6e7f", WorkspaceCollectionSweep.ActionSkippedNotOwned,
                null, WorkspaceCollectionSweep.ReasonNotOwned),
        };

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(entries, SqueezrJson.OutputOptions));
        var rows = document.RootElement.EnumerateArray().ToList();

        Keys(rows[0].ToString()).ShouldBe(["action", "collection", "endpoint"]);
        Keys(rows[1].ToString()).ShouldBe(["action", "collection", "endpoint", "failure"]);
        Keys(rows[2].ToString()).ShouldBe(["action", "collection", "endpoint", "reason"],
            "a skipped collection is not a failed delete: the refusal is its own field, so a reader never " +
            "counts a gate refusal in the failure total");
        rows[2].GetProperty("reason").GetString().ShouldBe("endpoint-not-owned");
    }

    [Fact]
    public void Wire_Fault_NamesTheEndpointItMeasured()
    {
        var fault = new WorkspaceRepairEndpointFault(ForeignEndpoint, "HttpRequestException: Connection refused");

        var json = JsonSerializer.Serialize(fault, SqueezrJson.OutputOptions);

        Keys(json).ShouldBe(["endpoint", "error"]);
        using var parsed = JsonDocument.Parse(json);
        parsed.RootElement.GetProperty("error").GetString()
            .ShouldBe("HttpRequestException: Connection refused",
                "the error text is the sweep's own measurement, passed through untouched");
    }

    [Fact]
    public void Wire_SkipVocabulary_IsTheUnclaimedDirectionAndNothingElse()
    {
        WorkspaceCollectionSweep.ReasonNotOwned.ShouldBe("endpoint-not-owned");
        WorkspaceCollectionSweep.ActionSkippedNotOwned.ShouldBe("skipped-not-owned");

        var sweepSource = RepoFile("src/Squeezr.Cli/Commands/WorkspaceCollectionSweep.cs");
        sweepSource.ShouldNotContain("provenance-known", Case.Insensitive,
            "the claim set answers forward from live rows only, so no wire word may promise it knows where a " +
            "name came from — unclaimed is the whole of what this sweep can say");
    }

    private static List<string> Keys(string json) =>
        [.. JsonDocument.Parse(json).RootElement.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)];

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            {
                return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        throw new InvalidOperationException($"Repository root not found above {AppContext.BaseDirectory}.");
    }
}

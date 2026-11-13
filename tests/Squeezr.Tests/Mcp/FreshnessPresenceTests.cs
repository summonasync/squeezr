using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Serialization;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Mcp;

public class FreshnessPresenceTests
{
    [Fact]
    public async Task IndexStatus_DeclaresWhyItHasNoPerFileVerdicts()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .ShouldContain(w => w.Contains("per-file freshness is not part of this aggregate report"));
    }

    [Fact]
    public async Task RepoMap_NamingStaleRows_StopsDenyingPerFileFreshness()
    {
        var workspace = IndexedWorkspace(("src/Alpha.cs", Class("Alpha")), ("src/Gone.cs", Class("Gone")));
        File.Delete(In(workspace, "src/Gone.cs"));
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var envelope = ParseEnvelope(await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?> { ["depth"] = 2 },
            cancellationToken: CancellationToken.None));

        var stale = envelope.GetProperty("staleFiles").EnumerateArray().Select(v => v.GetString()!).ToList();
        stale.ShouldContain("demo/src/Gone.cs", "the answer does carry a per-file verdict");
        Warnings(envelope).ShouldNotContain(w => w.Contains("per-file freshness is not attached", StringComparison.Ordinal),
            "the same call used to deny per-file freshness while shipping it");
        Freshness(envelope).GetProperty("check").GetString().ShouldBe("structure");
        Warnings(envelope).ShouldContain(w => w.Contains("structure, not content", StringComparison.Ordinal)
            && w.Contains("staleFiles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepoMap_CleanScope_DeclaresCoverageAnUnexaminedScopeCannot()
    {
        var clean = IndexedWorkspace(("src/Alpha.cs", Class("Alpha")), ("src/Beta.cs", Class("Beta")));
        var withheld = IndexedWorkspace(("src/Alpha.cs", Class("Alpha")), ("src/Beta.cs", Class("Beta")));
        // the rows exist from an index built before the widening, so the pass can neither stat nor name them:
        // coverage it does not have, which an empty staleFiles must not be allowed to hide
        SecretGuardFixture.SetGuardExtra(withheld, "**/*.cs");

        await using var cleanHarness = await McpTestHarness.StartAsync(clean);
        var cleanEnvelope = ParseEnvelope(await cleanHarness.Client.CallToolAsync(
            "repo_map", new Dictionary<string, object?> { ["depth"] = 2 }, cancellationToken: CancellationToken.None));
        var cleanClaim = Freshness(cleanEnvelope);

        await using var withheldHarness = await McpTestHarness.StartAsync(withheld);
        var withheldEnvelope = ParseEnvelope(await withheldHarness.Client.CallToolAsync(
            "repo_map", new Dictionary<string, object?> { ["depth"] = 2 }, cancellationToken: CancellationToken.None));
        var withheldClaim = Freshness(withheldEnvelope);

        Int(cleanClaim, "staleTotal").ShouldBe(0);
        Int(cleanClaim, "checked").ShouldBe(2, "a clean pass names nothing and still declares its coverage");
        Int(withheldClaim, "staleTotal").ShouldBe(0);
        Int(withheldClaim, "checked").ShouldBe(0);
        Int(withheldClaim, "scopeRows").ShouldBe(2,
            "the scope still speaks for two files this pass never reached — absence of a verdict, not a clean sheet");
        withheldEnvelope.GetProperty("staleFiles").EnumerateArray().ShouldBeEmpty();
        Warnings(cleanEnvelope).ShouldNotContain(w => w.Contains("answers structure, not content", StringComparison.Ordinal),
            "the class line speaks for a verdict the answer carries; on a scope that named none it is noise");
        cleanClaim.GetProperty("checked").GetInt32().ShouldNotBe(
            withheldClaim.GetProperty("checked").GetInt32(),
            "the two empty staleFiles answers are readable apart from the answer alone");
    }

    [Fact]
    public async Task RepoMap_ShortVerdictSample_IsMarkedIncompleteAndSplitsWhatItNames()
    {
        var vanished = Enumerable.Range(0, 40).Select(i => ($"src/Vanish{i:00}.cs", Class($"Vanish{i:00}"))).ToArray();
        var lagged = Enumerable.Range(0, 30).Select(i => ($"src/Lag{i:00}.cs", Class($"Lag{i:00}"))).ToArray();
        var steady = Enumerable.Range(0, 5).Select(i => ($"src/Keep{i:00}.cs", Class($"Keep{i:00}"))).ToArray();
        var workspace = IndexedWorkspace([.. vanished, .. lagged, .. steady]);
        foreach (var (relative, _) in vanished)
        {
            File.Delete(In(workspace, relative));
        }

        foreach (var (relative, _) in lagged)
        {
            File.AppendAllText(In(workspace, relative), "// past the stamp\n");
        }

        await using var harness = await McpTestHarness.StartAsync(workspace);
        var envelope = ParseEnvelope(await harness.Client.CallToolAsync(
            "repo_map", new Dictionary<string, object?> { ["depth"] = 2 }, cancellationToken: CancellationToken.None));
        var claim = Freshness(envelope);
        var stale = envelope.GetProperty("staleFiles").EnumerateArray().Select(v => v.GetString()!).ToList();

        Int(claim, "checked").ShouldBe(75);
        Int(claim, "scopeRows").ShouldBe(75);
        Int(claim, "staleTotal").ShouldBe(70);
        Int(claim, "listed").ShouldBe(stale.Count);
        Int(claim, "missing").ShouldBe(40);
        Int(claim, "changed").ShouldBe(30);
        Int(claim, "listedMissing").ShouldBe(25,
            "the list the answer carries is split by the same numbers the answer ships");
        stale.Take(25).ShouldAllBe(p => p.Contains("Vanish", StringComparison.Ordinal));
        stale.Skip(25).ShouldAllBe(p => p.Contains("Lag", StringComparison.Ordinal));

        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        envelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeFalse(
            "an answer that carries 50 of the 70 verdicts it found is not the whole story it names");
    }

    [Fact]
    public async Task RepoMap_Depth1Shape_SameDeclaredPrice_DifferentCompleteness()
    {
        // the pair review-017-03 measured on the poly-12k stand: a directory-only render prices the same
        // whether or not the scope behind it is stale, so the only honest difference left is completeness
        var names = Enumerable.Range(0, 75).Select(i => $"src/Unit{i:00}.cs").ToArray();
        var files = names.Select(p => (p, Class(Path.GetFileNameWithoutExtension(p)))).ToArray();
        var clean = IndexedWorkspace(files);
        var dirty = IndexedWorkspace(files);
        foreach (var relative in names[..40])
        {
            File.Delete(In(dirty, relative));
        }

        foreach (var relative in names[40..70])
        {
            File.AppendAllText(In(dirty, relative), "// past the stamp\n");
        }

        await using var cleanHarness = await McpTestHarness.StartAsync(clean);
        var cleanEnvelope = ParseEnvelope(await cleanHarness.Client.CallToolAsync(
            "repo_map", new Dictionary<string, object?> { ["depth"] = 1 }, cancellationToken: CancellationToken.None));

        await using var dirtyHarness = await McpTestHarness.StartAsync(dirty);
        var dirtyEnvelope = ParseEnvelope(await dirtyHarness.Client.CallToolAsync(
            "repo_map", new Dictionary<string, object?> { ["depth"] = 1 }, cancellationToken: CancellationToken.None));

        dirtyEnvelope.GetProperty("data").GetRawText().ShouldBe(cleanEnvelope.GetProperty("data").GetRawText(),
            "the same directory-only render, so every difference below is the verdict story and not the map");
        dirtyEnvelope.GetProperty("tokensUsed").GetInt32().ShouldBe(
            cleanEnvelope.GetProperty("tokensUsed").GetInt32());
        cleanEnvelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeTrue();
        dirtyEnvelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeFalse(
            "50 of 70 verdicts named is not the whole verdict, and the answer says so at the same price");
        Int(Freshness(dirtyEnvelope), "listed").ShouldBe(50);
        Int(Freshness(dirtyEnvelope), "staleTotal").ShouldBe(70);
        Int(Freshness(dirtyEnvelope), "checked").ShouldBe(75);
    }

    [Fact]
    public async Task RepoMap_Description_SaysStaleFilesCarriesASample_AndHowItsSeatsAreOrdered()
    {
        var workspace = IndexedWorkspace(("src/Alpha.cs", Class("Alpha")));
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var description = tools.Single(t => t.Name == "repo_map").Description!;

        description.ShouldContain("staleFiles");
        description.ShouldContain("sample");
        description.ShouldContain("freshness");
        // the schema has to say what listedMissing splits, or the number decodes only for a reader who
        // already knows how the sample was seated
        description.ShouldContain("listedMissing");
        description.ShouldContain("the vanished rows come first");
    }

    [Fact]
    public async Task CodeSearch_AnswerWiderThanTheCap_DeclaresHowMuchItChecked()
    {
        var workspace = WideIndexedWorkspace(FreshCandidateCap + 10);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var envelope = ParseEnvelope(await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "Add", ["maxTokens"] = 12_000 },
            cancellationToken: CancellationToken.None));
        var claim = Freshness(envelope);
        var named = Names(envelope);

        named.Count.ShouldBeGreaterThan(FreshCandidateCap);
        Int(claim, "namedFiles").ShouldBe(named.Count);
        Int(claim, "checked").ShouldBe(FreshCandidateCap, "the cap is the coverage, and the cap does not move here");
        claim.GetProperty("check").GetString().ShouldBe("content");
        claim.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["check", "checked", "namedFiles", "staleTotal", "listed", "laggingFiles", "freshnessTokens"],
            "a content answer has no vanished-or-lagged split to claim");
        Warnings(envelope).ShouldContain(
            w => w.StartsWith($"content check covered {FreshCandidateCap} of the {named.Count} files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CodeSearch_CapCoveredAnswer_ClaimsNoGap_AndStaysComplete()
    {
        var workspace = IndexedWorkspace(("src/Alpha.cs", Class("Alpha")), ("src/Beta.cs", Class("Beta")));
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var envelope = ParseEnvelope(await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "class" },
            cancellationToken: CancellationToken.None));
        var claim = Freshness(envelope);

        Int(claim, "checked").ShouldBe(Int(claim, "namedFiles"), "a covered answer says so with equal numbers");
        Warnings(envelope).ShouldNotContain(w => w.Contains("content check covered", StringComparison.Ordinal));
        envelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeTrue(
            "the whole verdict list fits in the answer, so nothing was left out");
    }

    // The answer-level shape of the refresh budget: files the check reached and the call refused to re-read
    // arrive named, counted, priced and still stale. Which of the two caps bound first is a wall-clock
    // property of the machine running the test, so this pins the band both caps imply, never one exact split.
    // The pin was authored against code_search (017/T4, review-017-06 NB-4); all three content tools run
    // the same capped guard and ship the same budget line, so the shape is pinned over the trio, not held
    // by reading for two of them.
    [Theory]
    [InlineData("code_search")]
    [InlineData("symbol_lookup")]
    [InlineData("find_references")]
    public async Task WhereTheBudgetRefusedAReRead_NamesIt_CountsIt_AndPricesIt(string tool)
    {
        var stale = RefreshBudget.MaxFilesReRead + 2;
        var workspace = WideIndexedWorkspace(stale);
        for (var i = 0; i < stale; i++)
        {
            File.AppendAllText(In(workspace, $"src/Unit{i:00}.cs"), "// past the stamp\n");
        }

        await using var harness = await McpTestHarness.StartAsync(workspace);
        var envelope = ParseEnvelope(await harness.Client.CallToolAsync(
            tool, BudgetRefusalArgs(tool), cancellationToken: CancellationToken.None));
        var claim = Freshness(envelope);
        var warnings = Warnings(envelope);

        Int(claim, "checked").ShouldBe(stale, "the budget stops re-reads, never examination");
        Int(claim, "namedFiles").ShouldBe(stale);
        var unrefreshed = Int(claim, "unrefreshedFiles");
        unrefreshed.ShouldBeGreaterThan(0,
            $"{stale} dirty candidates against a {RefreshBudget.MaxFilesReRead}-file cap cannot all be re-read");
        unrefreshed.ShouldBeInRange(
            stale - RefreshBudget.MaxFilesReRead, stale - 1,
            $"the count cap allows at most {RefreshBudget.MaxFilesReRead} re-reads, and the floor never refuses " +
            "the first one — a remainder outside this band is either a budget that does not mean what it says " +
            "or a re-read that threw, and this fixture has no reason to throw");

        Int(claim, "staleTotal").ShouldBe(unrefreshed,
            "no re-read failed on this fixture, so every stale row is a refusal and the two counts must agree");
        Int(claim, "listed").ShouldBe(unrefreshed);
        // the per-file lag claim only exists where the answer carries per-file rows: find_references over
        // uncalled definitions ships a data set with no file verdicts at all, and there a lagging count of
        // zero is the honest shape — an answer may only tally the files it actually calls lagging
        if (envelope.TryGetProperty("fileFreshness", out var verdicts) && verdicts.ValueKind == JsonValueKind.Array)
        {
            Int(claim, "laggingFiles").ShouldBe(unrefreshed,
                "a row the budget refused is lagging in fileFreshness too — the answer loses nothing");
        }
        else
        {
            Int(claim, "laggingFiles").ShouldBe(0,
                $"{tool}: no shipped fileFreshness verdicts means nothing is claimed as lagging");
        }

        envelope.GetProperty("staleFiles").EnumerateArray().Count().ShouldBe(unrefreshed);
        envelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeTrue(
            "complete is a claim about the verdict, and the verdict is named whole here — the budget is not a " +
            "withheld row, it is a refused re-read");

        claim.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["check", "checked", "namedFiles", "staleTotal", "listed", "laggingFiles", "unrefreshedFiles",
                "freshnessTokens"]);
        warnings.ShouldContain(w => w.StartsWith(
            $"refresh budget ({RefreshBudget.MaxFilesReRead} files / {RefreshBudget.MaxWallClockMillis} ms per call)",
            StringComparison.Ordinal));
        Int(claim, "freshnessTokens").ShouldBe(DeclaredByTheShippedBytes(envelope, claim, warnings),
            "the budget line is a shipped line, so it rides in the declared price like every other one");
    }

    // every tool names the same six Add definitions, so one symbol answers all three and the candidate
    // sets — and therefore the refusal counts — are identical across the trio
    private static Dictionary<string, object?> BudgetRefusalArgs(string tool) => tool switch
    {
        "symbol_lookup" => new() { ["name"] = "Add" },
        "find_references" => new() { ["symbol"] = "Add" },
        _ => new() { ["query"] = "Add" },
    };

    [Fact]
    public async Task CheckedTools_DeclareTheirClass_AggregateToolsDeclareNothing()
    {
        var workspace = IndexedWorkspace(("src/Calc.cs", Calculation));
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var checkedTools = new (string Tool, Dictionary<string, object?> Args)[]
        {
            ("repo_map", new() { ["depth"] = 2 }),
            ("code_search", new() { ["query"] = "Add" }),
            ("symbol_lookup", new() { ["name"] = "Calc" }),
            ("find_references", new() { ["symbol"] = "Add" }),
        };
        foreach (var (tool, args) in checkedTools)
        {
            var envelope = ParseEnvelope(await harness.Client.CallToolAsync(
                tool, args, cancellationToken: CancellationToken.None));
            var claim = Freshness(envelope);
            claim.GetProperty("check").GetString().ShouldBe(tool == "repo_map" ? "structure" : "content", tool);
            envelope.GetProperty("staleFiles").ValueKind.ShouldBe(JsonValueKind.Array, tool);
            Int(claim, "freshnessTokens").ShouldBeGreaterThan(0,
                $"{tool}: a story that ships is a story the caller pays for, whatever the scope looks like");
        }

        var aggregate = new (string Tool, Dictionary<string, object?> Args)[]
        {
            ("index_status", new()),
            ("expand_span", new() { ["file"] = "src/Calc.cs", ["startLine"] = 6, ["endLine"] = 9 }),
            ("code_pack", new() { ["name"] = "Add" }),
        };
        foreach (var (tool, args) in aggregate)
        {
            var envelope = ParseEnvelope(await harness.Client.CallToolAsync(
                tool, args, cancellationToken: CancellationToken.None));
            envelope.TryGetProperty("freshness", out _).ShouldBeFalse(
                $"{tool}: an answer with no check of its own declares no coverage");
            envelope.GetProperty("staleFiles").EnumerateArray().ShouldBeEmpty(tool);
        }
    }

    [Fact]
    public async Task RepoMap_NamingStaleRows_ShipsTheSeatPolicyInTheAnswer()
    {
        var vanished = Enumerable.Range(0, 40).Select(i => ($"src/Vanish{i:00}.cs", Class($"Vanish{i:00}"))).ToArray();
        var lagged = Enumerable.Range(0, 30).Select(i => ($"src/Lag{i:00}.cs", Class($"Lag{i:00}"))).ToArray();
        var workspace = IndexedWorkspace([.. vanished, .. lagged]);
        foreach (var (relative, _) in vanished)
        {
            File.Delete(In(workspace, relative));
        }

        foreach (var (relative, _) in lagged)
        {
            File.AppendAllText(In(workspace, relative), "// past the stamp\n");
        }

        await using var harness = await McpTestHarness.StartAsync(workspace);
        var envelope = ParseEnvelope(await MapAsync(harness, 2));

        // listedMissing decodes only against the seating order, so the order travels in shipped text — a C#
        // comment is not something a reader of the answer can hold
        var classLine = Warnings(envelope)
            .Single(w => w.Contains("answers structure, not content", StringComparison.Ordinal));
        classLine.ShouldContain("the vanished rows come first");
        var stale = envelope.GetProperty("staleFiles").EnumerateArray().Select(v => v.GetString()!).ToList();
        stale.Take(Int(Freshness(envelope), "listedMissing"))
            .ShouldAllBe(p => p.Contains("Vanish", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepoMap_ExactlyOneSampleOfStale_DeclaresWhatTheStoryCosts()
    {
        var lagged = Enumerable.Range(0, 50).Select(i => $"src/Lag{i:00}.cs").ToArray();
        var files = lagged
            .Select(p => (p, Class(Path.GetFileNameWithoutExtension(p))))
            .Append(("src/Keep.cs", Class("Keep")))
            .ToArray();
        var workspace = IndexedWorkspace(files);
        foreach (var relative in lagged)
        {
            File.AppendAllText(In(workspace, relative), "// past the stamp\n");
        }

        await using var harness = await McpTestHarness.StartAsync(workspace);
        var envelope = ParseEnvelope(await MapAsync(harness, 2));
        var claim = Freshness(envelope);
        var warnings = Warnings(envelope);

        Int(claim, "staleTotal").ShouldBe(50);
        Int(claim, "listed").ShouldBe(50);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        envelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeTrue(
            "complete stays a claim about the verdict; the price of the story is a number, not a flag");
        warnings.ShouldNotContain(w => w.Contains("stale rows are not listed", StringComparison.Ordinal),
            "nothing was left out, so the sample line has nothing to say");

        var declared = Int(claim, "freshnessTokens");
        declared.ShouldBe(DeclaredByTheShippedBytes(envelope, claim, warnings),
            "the declared price is the sample, the lines that explain the check and this block, on the wire basis");
        declared.ShouldBeGreaterThan(500, "the fifty-path sample alone is most of the story");
    }

    [Fact]
    public async Task RepoMap_CacheHit_MarksTheReplay_AndKeepsTheNumbersOfTheCallThatMadeThem()
    {
        var workspace = IndexedWorkspace(("src/Alpha.cs", Class("Alpha")), ("src/Beta.cs", Class("Beta")));
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var fresh = ParseEnvelope(await MapAsync(harness, 2));
        var first = Freshness(fresh);
        Int(first, "staleTotal").ShouldBe(0);
        first.TryGetProperty("replayed", out _).ShouldBeFalse("a call that ran the pass does not wear the mark");

        File.Delete(In(workspace, "src/Beta.cs"));

        var replay = ParseEnvelope(await MapAsync(harness, 2));
        var second = Freshness(replay);
        second.GetProperty("replayed").GetBoolean().ShouldBeTrue(
            "file mtimes move no cache generation, so this answer never looked at the disk");
        Int(second, "staleTotal").ShouldBe(0,
            "a replayed block describes the call that produced it — the mark is what tells the reader so");
        replay.GetProperty("staleFiles").EnumerateArray().ShouldBeEmpty();
        Int(second, "freshnessTokens").ShouldBe(
            Int(first, "freshnessTokens") + WireTokens(second.GetRawText()) - WireTokens(first.GetRawText()),
            "the mark ships bytes, and the declared price moves with the bytes that ship");

        var rebuilt = ParseEnvelope(await MapAsync(harness, 1));
        Int(Freshness(rebuilt), "staleTotal").ShouldBe(1, "one parameter away the same session runs its own check");
        Freshness(rebuilt).TryGetProperty("replayed", out _).ShouldBeFalse();

        QueryTelemetry.ReadAll(workspace).Single(e => e.CacheHit).TokensUsed.ShouldBe(
            replay.GetProperty("tokensUsed").GetInt32() + Int(second, "freshnessTokens"),
            "a replay costs what the replayed answer declares, not what the engine would have rebuilt");
    }

    [Fact]
    public async Task CodeSearch_StatVerdicts_AreNumberedEvenWhenTheGuardRefusedNothing()
    {
        var workspace = IndexedWorkspace(("src/Alpha.cs", Class("Alpha")), ("src/Beta.cs", Class("Beta")));
        // content untouched, clock moved: the hash guard calls both files fresh while the stat projection
        // says one of them lags — the answer must not read as a clean verdict while naming it
        File.SetLastWriteTimeUtc(In(workspace, "src/Beta.cs"), DateTime.UtcNow.AddDays(1));
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var envelope = ParseEnvelope(await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "Value" },
            cancellationToken: CancellationToken.None));
        var claim = Freshness(envelope);
        var verdicts = envelope.GetProperty("fileFreshness").EnumerateArray()
            .Select(row => row.GetProperty("verdict").GetString()).ToList();

        Int(claim, "staleTotal").ShouldBe(0);
        envelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeTrue();
        verdicts.ShouldContain(verdict => verdict == "lagging");
        Int(claim, "laggingFiles").ShouldBe(verdicts.Count(v => v == "lagging"),
            "the stat verdicts are part of what this answer claims, so they are counted");
    }

    [Fact]
    public async Task RepoMap_TelemetryRecordsTheNumberTheCallerActuallyPays()
    {
        var workspace = IndexedWorkspace(("src/Alpha.cs", Class("Alpha")), ("src/Gone.cs", Class("Gone")));
        File.Delete(In(workspace, "src/Gone.cs"));
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var envelope = ParseEnvelope(await MapAsync(harness, 2));
        var declared = Int(Freshness(envelope), "freshnessTokens");
        declared.ShouldBeGreaterThan(0);

        var entry = QueryTelemetry.ReadAll(workspace).Single(e => e.Tool == "repo_map");
        entry.TokensUsed.ShouldBe(envelope.GetProperty("tokensUsed").GetInt32() + declared,
            "squeezr stats may not report a cheaper answer than the caller was charged for");
        entry.Truncated.ShouldBe(envelope.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task CodeSearch_LiveWriterOnStore_NamesPidAndBeatAge()
    {
        var workspace = NewIndexedWorkspace();
        WriteLiveClaim(workspace);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var writerLine = envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .Single(w => w.StartsWith("active writer on this store:"));
        writerLine.ShouldContain($"pid {Environment.ProcessId}");
        // the age is wall-clock between the marker write and the tool call, floor-quantized to seconds —
        // an exact "0s" flaked under full-suite load (092). The notice only prints for a writer the probe
        // deems alive, so the printed age staying inside the stale bound is the invariant worth asserting.
        var beatAge = Regex.Match(writerLine, @"watch beat (\d+)s old");
        beatAge.Success.ShouldBeTrue(writerLine);
        int.Parse(beatAge.Groups[1].Value)
            .ShouldBeLessThanOrEqualTo((int)DaemonHeartbeat.StaleThreshold.TotalSeconds, writerLine);
    }

    [Fact]
    public async Task CodeSearch_NoClaim_NoWriterLine()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .Count(w => w.StartsWith("active writer on this store:"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task CodeSearch_StaleBeat_NoWriterLine()
    {
        var workspace = NewIndexedWorkspace();
        WriteLiveClaim(workspace, ageMinutes: 60);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .Count(w => w.StartsWith("active writer on this store:"))
            .ShouldBe(0);
    }

    private static void WriteLiveClaim(string workspace, double ageMinutes = 0)
    {
        var storeDir = SqueezrPaths.StoreDir(workspace);
        Directory.CreateDirectory(storeDir);
        var now = DateTime.UtcNow;
        var marker = new WatchOwnerMarker(
            Environment.ProcessId,
            Environment.MachineName,
            DaemonHeartbeat.DefaultKind,
            now.AddMinutes(-ageMinutes),
            now.AddMinutes(-ageMinutes));
        var line = JsonSerializer.Serialize(marker, SqueezrJson.FileOptions);
        File.WriteAllText(Path.Combine(storeDir, WatchOwnerMarker.MarkerFileName), line);
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private const int FreshCandidateCap = FreshnessGuard.CandidateLimit;

    private const string Calculation = """
        namespace Demo;

        public class Calc
        {
            /// <summary>Adds numbers.</summary>
            public int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    private static string IndexedWorkspace(params (string Path, string Content)[] files)
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        foreach (var (relative, content) in files)
        {
            WriteFile(workspace, $"repo/{relative}", content);
        }

        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    // wide enough that the candidate cap cannot reach every file the answer names
    private static string WideIndexedWorkspace(int files)
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        for (var i = 0; i < files; i++)
        {
            WriteFile(workspace, $"repo/src/Unit{i:00}.cs",
                $"public class Unit{i:00} {{ public int Add(int a, int b) => a + b; }}\n");
        }

        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string In(string workspace, string relative) =>
        Path.Combine(workspace, "repo", relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Class(string name) => $"public class {name} {{ public int Value = 1; }}\n";

    private static IReadOnlyList<string> Warnings(JsonElement envelope) =>
        envelope.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();

    private static JsonElement Freshness(JsonElement envelope) => envelope.GetProperty("freshness");

    private static async Task<CallToolResult> MapAsync(McpTestHarness harness, int depth) =>
        await harness.Client.CallToolAsync(
            "repo_map", new Dictionary<string, object?> { ["depth"] = depth }, cancellationToken: CancellationToken.None);

    // the answer re-measured from its own shipped bytes, on the basis the price claims to use: every quote
    // that travels inside the MCP content string costs six, the product's estimator prices ceil(len/4*1.1),
    // and each fragment is estimated on its own
    private static int WireTokens(string json) =>
        Math.Max(1, (int)Math.Ceiling((json.Length + 5 * json.Count(c => c == '\"')) / 4.0 * 1.1));

    private static int DeclaredByTheShippedBytes(JsonElement envelope, JsonElement claim, IReadOnlyList<string> warnings)
    {
        var story = WireTokens(envelope.GetProperty("staleFiles").GetRawText()) + WireTokens(claim.GetRawText());
        return warnings
            .Where(w => w.Contains("answers structure, not content", StringComparison.Ordinal) ||
                        w.Contains("stale rows are not listed", StringComparison.Ordinal) ||
                        w.Contains("refresh budget", StringComparison.Ordinal))
            .Aggregate(story, (total, line) => total + WireTokens("\"" + line + "\""));
    }

    private static int Int(JsonElement claim, string key) => claim.GetProperty(key).GetInt32();

    private static IReadOnlyList<string> Names(JsonElement envelope) =>
        envelope.GetProperty("data").EnumerateArray()
            .Select(hit => $"{hit.GetProperty("repo").GetString()}/{hit.GetProperty("file").GetString()}")
            .Distinct()
            .ToList();
}

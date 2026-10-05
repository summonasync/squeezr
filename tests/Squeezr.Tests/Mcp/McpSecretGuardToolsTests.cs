using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Mcp;

public class McpSecretGuardToolsTests
{
    [Fact]
    public async Task ExpandSpan_ProtectedFile_ReturnsRefusalErrorNotNotFound()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "expand_span",
            new Dictionary<string, object?> { ["file"] = SecretGuardFixture.EnvPath, ["startLine"] = 1 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = ErrorText(result);
        text.ShouldContain("protected by squeezr's built-in secret guard: never indexed, never rendered");
        text.ShouldNotContain("not in the index");
        text.ShouldNotContain(SecretGuardFixture.EnvCanary);
    }

    [Fact]
    public async Task ExpandSpan_ProtectedFileMissingFromDisk_StillRefuses()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        File.Delete(Path.Combine(workspace, "repo", SecretGuardFixture.PemPath));
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "expand_span",
            new Dictionary<string, object?> { ["file"] = SecretGuardFixture.PemPath, ["startLine"] = 1 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldBe(true);
        ErrorText(result).ShouldContain("built-in secret guard");
    }

    [Fact]
    public async Task ExpandSpan_ProtectedFileWithTrailingSeparator_ReturnsRefusalErrorNotNotFound()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "expand_span",
            new Dictionary<string, object?> { ["file"] = $"{SecretGuardFixture.PemPath}/", ["startLine"] = 1 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = ErrorText(result);
        text.ShouldContain("protected by squeezr's built-in secret guard");
        text.ShouldNotContain("not in the index");
    }

    [Fact]
    public async Task CodeSearch_OverProtectedText_WithholdsAndWarns()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = SecretGuardFixture.EnvCanary },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("data").EnumerateArray().ShouldBeEmpty();
        Warnings(envelope).ShouldContain(w => w.StartsWith("secret guard:", StringComparison.Ordinal));
        envelope.GetRawText().ShouldNotContain(SecretGuardFixture.EnvCanary);
    }

    [Fact]
    public async Task SymbolLookup_ProtectedDeclaration_WithholdsAndSkipsTheNotFoundHint()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "OPENAI_API_KEY", "variable");
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "OPENAI_API_KEY" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("data").EnumerateArray().ShouldBeEmpty();
        Warnings(envelope).ShouldContain(w => w.StartsWith("secret guard:", StringComparison.Ordinal));
        Warnings(envelope).ShouldNotContain("No symbol found — use code_search for paraphrase queries.");
        envelope.GetRawText().ShouldNotContain(SecretGuardFixture.EnvCanary);
    }

    [Fact]
    public async Task RepoMap_NeverNamesAProtectedPath()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?> { ["depth"] = 3 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var texts = envelope.GetProperty("data").EnumerateArray()
            .Select(line => line.GetProperty("text").GetString()!)
            .ToList();
        texts.ShouldNotContain(".env");
        texts.ShouldNotContain("monkey.pem");
        texts.ShouldNotContain("secrets.json");
        texts.ShouldNotContain("tokens.txt");
        texts.ShouldContain(".env.example");
        Warnings(envelope).ShouldContain(w => w.StartsWith("secret guard:", StringComparison.Ordinal));
        envelope.GetRawText().ShouldNotContain(SecretGuardFixture.PemCanary);
    }

    [Fact]
    public async Task FindReferences_WithholdsSitesInsideProtectedFiles()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantReferenceFrom(workspace, "Run", SecretGuardFixture.EnvPath, 1);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "Run" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("data").EnumerateArray()
            .Select(r => r.GetProperty("file").GetString()!)
            .ShouldNotContain(SecretGuardFixture.EnvPath);
        Warnings(envelope).ShouldContain(w => w.StartsWith("secret guard:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindReferences_SymbolDeclaredOnlyInSecrets_RefusesInsteadOfNotFound()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "DeployKey", "variable");
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "DeployKey" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = ErrorText(result);
        text.ShouldContain("protected by squeezr's built-in secret guard");
        text.ShouldNotContain("No symbol named");
    }

    [Fact]
    public async Task SymbolLookup_WideningTheGuardEvictsTheEnvelopeCachedUnderTheOlderPatternSet()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.TokensPath, "VaultToken", "variable");
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var first = await Lookup(harness);
        ParseEnvelope(first).GetProperty("data").EnumerateArray().ShouldNotBeEmpty();

        // same config, same call: this one IS served from cache — which is the leak the widening must kill
        var warmed = await Lookup(harness);
        ParseEnvelope(warmed).GetProperty("data").EnumerateArray().ShouldNotBeEmpty();

        // pin the config clock back: here ONLY the pattern set carried in the cache key may evict the entry,
        // which is what separates the key mechanism from the generation mechanism (see the narrowing test)
        var configPath = SqueezrPaths.ConfigPath(workspace);
        var stamp = File.GetLastWriteTimeUtc(configPath);
        var generation = File.GetLastWriteTimeUtc(SqueezrPaths.MetaDbPath(workspace));
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        File.SetLastWriteTimeUtc(configPath, stamp);

        File.GetLastWriteTimeUtc(SqueezrPaths.MetaDbPath(workspace)).ShouldBe(generation);
        File.GetLastWriteTimeUtc(configPath).ShouldBe(stamp);

        var envelope = ParseEnvelope(await Lookup(harness));
        envelope.GetProperty("data").EnumerateArray().ShouldBeEmpty();
        Warnings(envelope).ShouldContain(w => w.StartsWith("secret guard:", StringComparison.Ordinal));
        envelope.GetRawText().ShouldNotContain(SecretGuardFixture.TokensCanary);

        var lookups = QueryTelemetry.ReadAll(workspace).Where(e => e.Tool == "symbol_lookup").ToList();
        lookups.Count.ShouldBe(3);
        lookups[0].CacheHit.ShouldBeFalse();
        lookups[1].CacheHit.ShouldBeTrue();
        lookups[2].CacheHit.ShouldBeFalse();
    }

    [Fact]
    public async Task CodeSearch_WideningTheGuardEvictsTheEnvelopeCachedUnderTheOlderPatternSet()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);
        var parameters = new Dictionary<string, object?> { ["query"] = SecretGuardFixture.TokensCanary };

        ParseEnvelope(await harness.Client.CallToolAsync("code_search", parameters,
            cancellationToken: CancellationToken.None)).GetProperty("data").EnumerateArray().ShouldNotBeEmpty();

        var configPath = SqueezrPaths.ConfigPath(workspace);
        var stamp = File.GetLastWriteTimeUtc(configPath);
        var generation = File.GetLastWriteTimeUtc(SqueezrPaths.MetaDbPath(workspace));
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        File.SetLastWriteTimeUtc(configPath, stamp);

        File.GetLastWriteTimeUtc(SqueezrPaths.MetaDbPath(workspace)).ShouldBe(generation);
        File.GetLastWriteTimeUtc(configPath).ShouldBe(stamp);

        var envelope = ParseEnvelope(await harness.Client.CallToolAsync("code_search", parameters,
            cancellationToken: CancellationToken.None));
        envelope.GetProperty("data").EnumerateArray().ShouldBeEmpty();
        Warnings(envelope).ShouldContain(w => w.StartsWith("secret guard:", StringComparison.Ordinal));
        envelope.GetRawText().ShouldNotContain(SecretGuardFixture.TokensCanary);
    }

    [Fact]
    public async Task EveryTool_CanaryNeverCrossesTheWire()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "OPENAI_API_KEY", "variable");
        SecretGuardFixture.PlantReferenceFrom(workspace, "Run", SecretGuardFixture.EnvPath, 1);
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var outputs = new List<string>
        {
            Raw(await harness.Client.CallToolAsync(
                "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None)),
            Raw(await harness.Client.CallToolAsync(
                "repo_map", new Dictionary<string, object?> { ["depth"] = 3 }, cancellationToken: CancellationToken.None)),
            Raw(await harness.Client.CallToolAsync(
                "code_search", new Dictionary<string, object?> { ["query"] = "CANARY" }, cancellationToken: CancellationToken.None)),
            Raw(await harness.Client.CallToolAsync(
                "code_search",
                new Dictionary<string, object?> { ["query"] = "tokens vault SS4" },
                cancellationToken: CancellationToken.None)),
            Raw(await harness.Client.CallToolAsync(
                "symbol_lookup", new Dictionary<string, object?> { ["name"] = "OPENAI_API_KEY" }, cancellationToken: CancellationToken.None)),
            Raw(await harness.Client.CallToolAsync(
                "find_references", new Dictionary<string, object?> { ["symbol"] = "Run" }, cancellationToken: CancellationToken.None)),
            Raw(await harness.Client.CallToolAsync(
                "expand_span",
                new Dictionary<string, object?> { ["file"] = SecretGuardFixture.EnvPath, ["startLine"] = 1 },
                cancellationToken: CancellationToken.None)),
        };

        var haystack = string.Join("\n", outputs);
        foreach (var canary in SecretGuardFixture.ProtectedCanaries)
        {
            haystack.ShouldNotContain(canary);
        }
    }

    [Fact]
    public async Task SymbolLookup_NarrowingTheGuardBackDoesNotServeTheEnvelopeCachedBeforeTheWidening()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.TokensPath, "VaultToken", "variable");
        await using var harness = await McpTestHarness.StartAsync(workspace);

        // no extensions: the answer is served, then cached under the key that means "extensions empty"
        ParseEnvelope(await Lookup(harness)).GetProperty("data").EnumerateArray().ShouldNotBeEmpty();
        ParseEnvelope(await Lookup(harness)).GetProperty("data").EnumerateArray().ShouldNotBeEmpty();

        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        ParseEnvelope(await Lookup(harness)).GetProperty("data").EnumerateArray().ShouldBeEmpty();

        // narrowing reconstitutes the ORIGINAL key, so only the generation can keep the pre-widening
        // envelope out of the answer — and no index or watch pass happened in between
        SecretGuardFixture.SetGuardExtra(workspace);
        var narrowed = ParseEnvelope(await Lookup(harness));

        var lookups = QueryTelemetry.ReadAll(workspace).Where(e => e.Tool == "symbol_lookup").ToList();
        lookups.Count.ShouldBe(4);
        lookups.Select(e => e.CacheHit).ToArray().ShouldBe([false, true, false, false]);

        // the mask is gone again, so serving the file is what the config now says — the point the CacheHit
        // line pins is provenance: this answer was read now, not resurrected from the pre-widening entry
        narrowed.GetProperty("data").EnumerateArray().ShouldNotBeEmpty();
        Warnings(narrowed).ShouldNotContain(w => w.StartsWith("secret guard:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuardPaths_AppearInWarningsAndNeverInAResultField()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "OPENAI_API_KEY", "variable");
        SecretGuardFixture.PlantReferenceFrom(workspace, "Run", SecretGuardFixture.EnvPath, 1);
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var hidden = new[]
        {
            SecretGuardFixture.EnvPath,
            SecretGuardFixture.PemPath,
            SecretGuardFixture.SecretsPath,
            SecretGuardFixture.TokensPath,
        };
        var hiddenDirs = new[] { "config/", "keys/", "notes/" };

        var calls = new (string Tool, Dictionary<string, object?> Arguments)[]
        {
            ("repo_map", new Dictionary<string, object?> { ["depth"] = 3 }),
            ("code_search", new Dictionary<string, object?> { ["query"] = "CANARY" }),
            ("code_search", new Dictionary<string, object?> { ["query"] = "vault tokens SS4" }),
            ("symbol_lookup", new Dictionary<string, object?> { ["name"] = "OPENAI_API_KEY" }),
            ("find_references", new Dictionary<string, object?> { ["symbol"] = "Run" }),
        };

        foreach (var (tool, arguments) in calls)
        {
            var envelope = ParseEnvelope(await harness.Client.CallToolAsync(
                tool, arguments, cancellationToken: CancellationToken.None));

            Warnings(envelope).Any(w => w.StartsWith("secret guard:", StringComparison.Ordinal))
                .ShouldBeTrue($"{tool} owes a withheld count");

            AssertAnswersNeverNameTheSecret(tool, envelope, hidden, hiddenDirs);

            foreach (var canary in SecretGuardFixture.ProtectedCanaries)
            {
                envelope.GetRawText().ShouldNotContain(canary, customMessage: $"{tool} rendered a protected value");
            }
        }

        // the ledger tool owes no withheld count, but it is the field set the guard wall has to hold for
        // too, so it rides the same sweep
        AssertAnswersNeverNameTheSecret("index_status",
            ParseEnvelope(await harness.Client.CallToolAsync(
                "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None)),
            hidden, hiddenDirs);
    }

    [Fact]
    public async Task SymbolLookup_SuggestionSweepStopsAtTheGuard()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.TokensPath, "VaultToken", "variable");
        await using var harness = await McpTestHarness.StartAsync(workspace);

        // the near name really does surface while the file is unprotected, so the widened call below fails
        // on the guard rather than on a suggestion path that never worked
        AnswerText(ParseEnvelope(await Suggest(harness))).ShouldContain("VaultToken");

        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");

        var answer = AnswerText(ParseEnvelope(await Suggest(harness)));

        answer.ShouldNotContain("VaultToken");
        answer.ShouldNotContain(SecretGuardFixture.TokensPath);
    }

    // Whole-envelope sweep instead of a hand-kept map of the fields that name files: every additive field
    // (definitions, staleFiles, whatever ships next) is covered the moment it appears, instead of the
    // moment somebody remembers to extend the map.
    private static void AssertAnswersNeverNameTheSecret(
        string tool,
        JsonElement envelope,
        IReadOnlyList<string> hiddenPaths,
        IReadOnlyList<string> hiddenDirs)
    {
        var answer = AnswerText(envelope);

        foreach (var path in hiddenPaths)
        {
            answer.ShouldNotContain(path, customMessage: $"{tool} named a protected path outside its warnings");
            answer.ShouldNotContain(
                System.IO.Path.GetFileName(path),
                customMessage: $"{tool} named a protected file outside its warnings");
        }

        foreach (var dir in hiddenDirs)
        {
            answer.ShouldNotContain(dir, customMessage: $"{tool} named a directory that only holds protected files");
        }
    }

    // The withheld sentence is the one place a protected path is required to appear, so exactly those lines are
    // cut out of the sweep — every other warning stays under it, or the wall would silently stop covering a
    // future warning that quotes a path. The conventional .env placeholders are masked because each of them
    // literally contains a protected name (SecretGuard.EnvCarveOuts) while staying in the answer on purpose.
    private static string AnswerText(JsonElement envelope)
    {
        var answer = JsonNode.Parse(envelope.GetRawText())!.AsObject();
        if (answer["warnings"] is JsonArray warnings)
        {
            answer["warnings"] =
                new JsonArray([.. warnings.Where(line => !IsWithheldCount(line)).Select(line => line?.DeepClone())]);
        }

        var text = answer.ToJsonString();
        foreach (var placeholder in new[] { ".env.example", ".env.sample", ".env.template" })
        {
            text = text.Replace(placeholder, string.Empty, StringComparison.Ordinal);
        }

        return text;
    }

    private static bool IsWithheldCount(JsonNode? line) =>
        line is JsonValue value
        && value.TryGetValue<string>(out var text)
        && text.StartsWith("secret guard:", StringComparison.Ordinal);

    [Fact]
    public void AnswerText_CutsOnlyTheWithheldSentenceOutOfTheSweep()
    {
        var envelope = JsonDocument.Parse("""
            {"data":[],"warnings":[
              "secret guard: 1 result(s) withheld ('.env' matches built-in patterns)",
              "vector store is unavailable, falling back to exact search only"]}
            """).RootElement;

        var answer = AnswerText(envelope);

        answer.ShouldContain("vector store is unavailable");
        answer.ShouldNotContain(".env");
    }

    private static ValueTask<CallToolResult> Lookup(McpTestHarness harness) =>
        harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "VaultToken" },
            cancellationToken: CancellationToken.None);

    private static ValueTask<CallToolResult> Suggest(McpTestHarness harness) =>
        harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "VaultTokens" },
            cancellationToken: CancellationToken.None);

    private static string ErrorText(CallToolResult result) =>
        result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text;

    private static string Raw(CallToolResult result) =>
        string.Join("\n", result.Content.Select(block => block.ToString() ?? string.Empty));

    private static IReadOnlyList<string> Warnings(JsonElement envelope) =>
        envelope.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}

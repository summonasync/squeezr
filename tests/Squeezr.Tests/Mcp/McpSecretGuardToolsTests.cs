using System.Text.Json;
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
            var named = NamedFields(tool, envelope);

            Warnings(envelope).Any(w => w.StartsWith("secret guard:", StringComparison.Ordinal))
                .ShouldBeTrue($"{tool} owes a withheld count");

            foreach (var path in hidden)
            {
                named.Contains(path).ShouldBeFalse($"{tool} put a protected path in a result field");
                named.Contains(System.IO.Path.GetFileName(path))
                    .ShouldBeFalse($"{tool} put a protected file name in a result field");
            }

            named.Any(text => hiddenDirs.Any(dir => text.Contains(dir, StringComparison.Ordinal)))
                .ShouldBeFalse($"{tool} put a directory that only holds protected files in a result field");

            envelope.GetProperty("data").GetRawText()
                .ShouldNotContain("CANARY-ENV-VALUE", customMessage: "data holds values, warnings hold paths");
        }
    }

    // the strings a result structure uses to name files: tree labels, hit paths, match files, ref files
    private static IReadOnlyList<string> NamedFields(string tool, JsonElement envelope) =>
        tool switch
        {
            "repo_map" => [.. envelope.GetProperty("data").EnumerateArray()
                .Select(line => line.GetProperty("text").GetString()!)],
            "code_search" => [.. envelope.GetProperty("data").EnumerateArray()
                .Select(hit => hit.GetProperty("file").GetString()!)],
            "symbol_lookup" => [.. envelope.GetProperty("data").EnumerateArray()
                .Select(match => match.GetProperty("file").GetString()!)],
            "find_references" => [.. envelope.GetProperty("data").EnumerateArray()
                .Select(reference => reference.GetProperty("file").GetString()!)],
            _ => throw new InvalidOperationException($"no field mapping for {tool}"),
        };

    private static ValueTask<CallToolResult> Lookup(McpTestHarness harness) =>
        harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "VaultToken" },
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

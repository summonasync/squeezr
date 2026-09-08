using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class McpPolyglotToolsTests
{
    [Fact]
    public async Task SymbolLookup_FindsJsFunctionWithSignatureAndSpan()
    {
        var workspace = NewPolyglotWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "mul", ["maxTokens"] = 500 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var hit = envelope.GetProperty("data").EnumerateArray().ShouldHaveSingleItem();
        hit.GetProperty("repo").GetString().ShouldBe("demo");
        hit.GetProperty("file").GetString().ShouldBe("src/util.js");
        hit.GetProperty("signature").GetString()!.ShouldContain("function mul(a, b)");
        hit.GetProperty("span").GetString()!.ShouldContain("-");
        envelope.GetProperty("tokensUsed").GetInt32().ShouldBeGreaterThan(0);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task SymbolLookup_FindsTsClassWithInterface()
    {
        var workspace = NewPolyglotWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Store", ["maxTokens"] = 500 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var hit = envelope.GetProperty("data").EnumerateArray().ShouldHaveSingleItem();
        hit.GetProperty("file").GetString().ShouldBe("src/store.ts");
        hit.GetProperty("signature").GetString()!.ShouldContain("class Store");
    }

    [Fact]
    public async Task CodeSearch_FindsJsContent()
    {
        var workspace = NewPolyglotWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "uniqjsneedle" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var hit = envelope.GetProperty("data").EnumerateArray().ShouldHaveSingleItem();
        hit.GetProperty("repo").GetString().ShouldBe("demo");
        hit.GetProperty("file").GetString().ShouldBe("src/app.js");
        hit.GetProperty("source").GetString()!.ShouldBe("exact");
        hit.GetProperty("snippet").GetString()!.ShouldContain("uniqjsneedle");
    }

    [Fact]
    public async Task FindReferences_JsImportChain_Reachable()
    {
        var workspace = NewPolyglotWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "mul" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var refs = envelope.GetProperty("data").EnumerateArray().ToList();
        refs.ShouldContain(r =>
            r.GetProperty("file").GetString() == "src/app.js" &&
            r.GetProperty("line").GetInt32() == 1 &&
            r.GetProperty("kind").GetString() == "import");
        refs.ShouldContain(r =>
            r.GetProperty("file").GetString() == "src/app.js" &&
            r.GetProperty("line").GetInt32() == 9 &&
            r.GetProperty("kind").GetString() == "call" &&
            r.GetProperty("enclosingSymbol").GetString() == "Calc.double");
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string NewPolyglotWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        WriteFile(workspace, "repo/src/Calc.cs", "class Calc {\n    int Add(int a, int b) {\n        return a + b;\n    }\n}\n");
        WriteFile(workspace, "repo/src/util.js", "export function mul(a, b) {\n  return a * b;\n}\n");
        WriteFile(workspace, "repo/src/app.js", """
            import { mul } from './util';

            export function add(a, b) {
              return a + b;
            }

            export class Calc {
              double(n) {
                const scaled = mul(2, n); // uniqjsneedle
                return scaled;
              }
            }
            """);
        WriteFile(workspace, "repo/src/store.ts", """
            interface User {
              id: number;
            }

            export class Store {
              find(id: number): User | undefined {
                return undefined;
              }
            }
            """);
        WriteFile(workspace, "repo/README.md", "polyglot demo\nuniqmdneedle\n");
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}

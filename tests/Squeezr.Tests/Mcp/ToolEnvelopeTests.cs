using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Mcp;

public class ToolEnvelopeTests
{
    [Fact]
    public void Serialize_UsesWireContractNames()
    {
        var envelope = ToolEnvelope.Wrap(
            new { hit = 1 },
            tokensUsed: 7,
            truncated: true,
            warnings: ["w1"],
            workspaceRoot: TestDirs.NewTempDir(),
            staleFiles: ["demo/a.cs"]);

        var json = JsonSerializer.Serialize(envelope);

        json.ShouldContain("\"data\":");
        json.ShouldContain("\"tokensUsed\":7");
        json.ShouldContain("\"truncated\":true");
        json.ShouldContain("\"warnings\":[\"w1\"]");
        json.ShouldContain("\"indexAge\":null");
        json.ShouldContain("\"staleFiles\":[\"demo/a.cs\"]");
    }

    [Fact]
    public void Wrap_WithoutMetaDb_IndexAgeIsNull()
    {
        var envelope = ToolEnvelope.Wrap(null, 0, false, workspaceRoot: TestDirs.NewTempDir());

        envelope.IndexAge.ShouldBeNull();
    }

    [Fact]
    public void Wrap_WithMetaDb_IndexAgeIsPositive()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(SqueezrPaths.MetaDbPath(root), string.Empty);

        var envelope = ToolEnvelope.Wrap(null, 0, false, workspaceRoot: root);

        envelope.IndexAge.ShouldNotBeNull();
        envelope.IndexAge!.Value.ShouldBeGreaterThan(TimeSpan.Zero);
    }
}

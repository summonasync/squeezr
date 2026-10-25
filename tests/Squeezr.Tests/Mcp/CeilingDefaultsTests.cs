using Shouldly;
using Squeezr.Cli.Mcp.Tools;
using Squeezr.Core.Indexing.Navigation;

namespace Squeezr.Tests.Mcp;

public class CeilingDefaultsTests
{
    [Fact]
    public void ToolCeilings_StayAtTunedDefaults()
    {
        SymbolLookup.DefaultMaxTokens.ShouldBe(300);
        RepoMapper.DefaultMaxTokens.ShouldBe(4000);
        SpanExpander.DefaultMaxTokens.ShouldBe(1500);
        CodeSearchDefault().ShouldBe(2000);
    }

    private static int CodeSearchDefault() =>
        (int)typeof(CodeSearchTool)
            .GetField("DefaultMaxTokens", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
}

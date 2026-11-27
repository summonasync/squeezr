using Shouldly;

namespace Squeezr.Tests.Structure;

// The 130/T1 carve contract, enforced where the carve cannot quietly rot: Core sources name no
// semantic type, and the project graph keeps Semantic behind Cli. Lite (T3) builds on exactly this.
public class CoreSemanticFreedomTests
{
    public const string SemanticTokenPattern = "Qdrant|Ollama|IVectorStore|Embedding";

    [Fact]
    public void Core_Sources_NameNoSemanticToken()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(CoreSrcRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(CoreSrcRoot(), file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/bin/") || relative.Contains("/obj/"))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(lines[i], SemanticTokenPattern))
                {
                    offenders.Add($"{relative}:{i + 1}");
                }
            }
        }

        offenders.ShouldBe([], $"Squeezr.Core must stay semantic-free, but these sites name the semantic domain: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Core_Project_DoesNotReferenceSemantic()
    {
        // exposing internals to the implementing assembly is fine; depending on it is not
        File.ReadAllText(CoreCsproj())
            .ShouldNotContain(@"<ProjectReference Include=""..\Squeezr.Semantic");
    }

    [Fact]
    public void ProjectGraph_SemanticSitsBehindCli()
    {
        var semantic = File.ReadAllText(Path.Combine(SrcRoot(), "Squeezr.Semantic", "Squeezr.Semantic.csproj"));
        var cli = File.ReadAllText(Path.Combine(SrcRoot(), "Squeezr.Cli", "Squeezr.Cli.csproj"));

        semantic.ShouldContain(@"Include=""..\Squeezr.Core\Squeezr.Core.csproj""");
        cli.ShouldContain(@"Include=""..\Squeezr.Semantic\Squeezr.Semantic.csproj""");
    }

    private static string SrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("the solution root was not found from the test binaries");
        return Path.Combine(dir!.FullName, "src");
    }

    private static string CoreSrcRoot() => Path.Combine(SrcRoot(), "Squeezr.Core");

    private static string CoreCsproj() => Path.Combine(CoreSrcRoot(), "Squeezr.Core.csproj");
}

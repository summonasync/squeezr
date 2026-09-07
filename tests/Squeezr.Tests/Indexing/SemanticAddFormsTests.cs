using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public class SemanticAddFormsTests
{
    [Theory]
    [InlineData("dot")]
    [InlineData("subfolder")]
    [InlineData("absolute")]
    public void Index_AllAddForms_RunSemanticPhaseWithSymbolsAndChunks(string form)
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);

        var repoDir = form == "dot" ? workspace : Path.Combine(workspace, "repo");
        WriteFile(repoDir, "Slurpy.slnx", """
            <Solution>
              <Project Path="src/App.csproj" />
            </Solution>
            """);
        WriteFile(repoDir, "src/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        WriteFile(repoDir, "src/Calc.cs", """
            public class Calc
            {
                public int Add(int a, int b) => a + b;
            }
            """);

        var addPath = form switch
        {
            "dot" => ".",
            "subfolder" => "repo",
            _ => repoDir
        };
        manager.AddRepo(addPath, "demo");

        var result = new WorkspaceIndexer().Index(workspace);

        result.Warnings.ShouldNotContain(w => w.Contains("semantic index skipped"));
        var repo = manager.GetStatus().Repos.ShouldHaveSingleItem();
        repo.Symbols.ShouldBeGreaterThan(0);
        repo.Chunks.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Index_WithoutProjectFiles_ForwardsSessionDegradationWarning()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        WriteFile(workspace, "src/Calc.cs", """
            public class Calc
            {
                public int Add(int a, int b) => a + b;
            }
            """);
        manager.AddRepo(".", "demo");

        var result = new WorkspaceIndexer().Index(workspace);

        result.Warnings.ShouldContain(w => w.StartsWith("MSBuild workspace unavailable"));
        result.Warnings.ShouldNotContain(w => w.StartsWith("ad-hoc workspace"));
        result.Warnings.ShouldNotContain(w => w.StartsWith("no reference assemblies on disk"));
        var repo = manager.GetStatus().Repos.ShouldHaveSingleItem();
        repo.Symbols.ShouldBeGreaterThan(0);
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}

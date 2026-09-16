using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Query;

public class SearchSemanticRankingTests
{
    [Fact]
    public void Ranking_SignatureMatchBeatsBodyMatch_CommentMatchDemoted()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/sig.cs", """
            public class Api
            {
                public int Zanzibar(int quota)
                {
                    return quota;
                }
            }
            """);
        WriteFile(workspace, "repo/body.cs", """
            public class Worker
            {
                public void Run()
                {
                    var text = "zanzibar zanzibar";
                }
            }
            """);
        WriteFile(workspace, "repo/comment.cs", """
            // zanzibar helper
            public class Notes
            {
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        var response = new SearchEngine(workspace).Search(
            new SearchQuery("zanzibar", false, null, null, null, null, null, null));

        var paths = response.Matches.Select(m => m.Path).ToList();
        paths.ShouldBe(["sig.cs", "body.cs", "comment.cs"]);
        response.Matches[0].Score.ShouldBeGreaterThan(response.Matches[1].Score);
        response.Matches[1].Score.ShouldBeGreaterThan(response.Matches[2].Score);
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}

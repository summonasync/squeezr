using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Navigation;

public class IndexNotBuiltThrowSiteTests
{
    private const string Sentence = "Index is not built. Run 'squeezr index' first.";

    [Fact]
    public void RequireIndex_UnbuiltWorkspace_ThrowsTheIndexNotBuiltCode()
    {
        var root = UnbuiltWorkspace();

        var ex = Should.Throw<WorkspaceException>(() => NavigationContext.RequireIndex(root));

        ex.Code.ShouldBe(WorkspaceException.IndexNotBuiltCode);
        ex.Code.ShouldNotBe(WorkspaceException.DefaultCode);
        ex.Message.ShouldBe(Sentence);
    }

    [Fact]
    public void ExactSearchPlan_UnbuiltWorkspace_ThrowsTheSameCodeAndSentence()
    {
        var root = UnbuiltWorkspace();

        var ex = Should.Throw<WorkspaceException>(() => ExactSearchPlan.Execute(
            root,
            new SearchQuery("Add", false, null, null, null, null, null, 200),
            trackChunkKeys: false,
            fileScope: null,
            guard: SecretGuardReader.Load(root)));

        ex.Code.ShouldBe(WorkspaceException.IndexNotBuiltCode);
        ex.Message.ShouldBe(NavigationContext.MissingIndexMessage);
    }

    [Fact]
    public void RequireIndex_IndexPresent_ReturnsTheRootAndNeverThrowsTheCode()
    {
        var root = UnbuiltWorkspace();
        File.WriteAllText(SqueezrPaths.TrigramIndexPath(root), "not-a-real-index");

        NavigationContext.RequireIndex(root).ShouldBe(Path.GetFullPath(root));
    }

    [Fact]
    public void MissingIndexMessage_IsTheSentenceTheEnvelopeTextCarries() =>
        NavigationContext.MissingIndexMessage.ShouldBe(Sentence);

    private static string UnbuiltWorkspace()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "Calc.cs"), "public class Calc { public int Add() => 1; }\n");
        new WorkspaceManager(root).Init(null);
        ConfigFile.Save(root, ConfigFile.Load(root) with
        {
            Embeddings = new EmbeddingsConfig { Provider = "onnx" }
        });
        File.Exists(SqueezrPaths.TrigramIndexPath(root)).ShouldBeFalse();
        return root;
    }
}

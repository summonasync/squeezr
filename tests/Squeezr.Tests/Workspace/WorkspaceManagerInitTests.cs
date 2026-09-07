using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Workspace;

public class WorkspaceManagerInitTests
{
    [Fact]
    public void Init_CreatesWorkspaceLayout()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);

        var result = manager.Init(null);

        result.WorkspaceName.ShouldBe(new DirectoryInfo(root).Name);
        result.RootPath.ShouldBe(Path.GetFullPath(root));

        File.Exists(Path.Combine(root, "squeezr.workspace.json")).ShouldBeTrue();
        File.Exists(SqueezrPaths.ConfigPath(root)).ShouldBeTrue();
        File.Exists(SqueezrPaths.MetaDbPath(root)).ShouldBeTrue();
        File.Exists(SqueezrPaths.StatePath(root)).ShouldBeTrue();
        File.Exists(SqueezrPaths.ComposePath(root)).ShouldBeTrue();
        Directory.Exists(SqueezrPaths.StoreDir(root)).ShouldBeTrue();
    }

    [Fact]
    public void Init_UsesProvidedName()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);

        var result = manager.Init("my-system");

        result.WorkspaceName.ShouldBe("my-system");
        WorkspaceManifestFile.Load(root).Name.ShouldBe("my-system");
    }

    [Fact]
    public void Init_Twice_Throws()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);
        manager.Init(null);

        Should.Throw<WorkspaceException>(() => manager.Init(null));
    }

    [Fact]
    public void Init_WritesSpecCompliantManifest()
    {
        var root = TestDirs.NewTempDir();
        new WorkspaceManager(root).Init("my-system");

        using var doc = JsonDocument.Parse(File.ReadAllText(SqueezrPaths.ManifestPath(root)));
        doc.RootElement.GetProperty("name").GetString().ShouldBe("my-system");
        doc.RootElement.GetProperty("repos").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void Init_WritesSpecCompliantStateFile()
    {
        var root = TestDirs.NewTempDir();
        new WorkspaceManager(root).Init(null);

        var state = StateFile.Load(root);
        state.Version.ShouldBe(1);
        state.Repos.ShouldBeEmpty();
    }

    [Fact]
    public void Init_WritesDefaultConfigMatchingSpec()
    {
        var root = TestDirs.NewTempDir();
        new WorkspaceManager(root).Init(null);

        var config = ConfigFile.Load(root);

        config.Embeddings.Provider.ShouldBe("onnx");
        config.Embeddings.Onnx.Dimensions.ShouldBe(1536);
        config.Embeddings.Onnx.MaxSequenceLength.ShouldBe(512);
        config.Embeddings.Onnx.BatchSize.ShouldBe(32);
        config.Embeddings.Onnx.ModelPath.ShouldBe("~/.squeezr/models/bge-code-v1.onnx");
        config.Embeddings.OpenAiCompatible.Endpoint.ShouldBe("https://api.openai.com/v1");
        config.Embeddings.OpenAiCompatible.ApiKeyEnv.ShouldBe("SQUEEZR_API_KEY");
        config.Embeddings.OpenAiCompatible.Model.ShouldBe("text-embedding-3-large");
        config.Embeddings.OpenAiCompatible.Dimensions.ShouldBe(1024);
        config.Chunking.MaxTokensPerChunk.ShouldBe(512);
        config.Chunking.FallbackWindowLines.ShouldBe(60);
        config.Chunking.FallbackOverlapLines.ShouldBe(15);
        config.Indexing.Ignore.ShouldBe(["**/node_modules/**", "**/bin/**", "**/obj/**", "**/dist/**", "**/.vs/**", "**/.idea/**", "**/*.min.js"]);
        config.Indexing.MaxFileSizeKb.ShouldBe(512);
        config.Indexing.GitAware.ShouldBeTrue();
        config.Qdrant.Endpoint.ShouldBe("http://localhost:6334");
    }

    [Fact]
    public void Init_GeneratesQdrantComposeFile()
    {
        var root = TestDirs.NewTempDir();
        new WorkspaceManager(root).Init(null);

        var compose = File.ReadAllText(SqueezrPaths.ComposePath(root));
        compose.ShouldContain("qdrant/qdrant:latest");
        compose.ShouldContain("6333:6333");
        compose.ShouldContain("6334:6334");
        compose.ShouldContain("squeezr-qdrant:/qdrant/storage");
    }
}

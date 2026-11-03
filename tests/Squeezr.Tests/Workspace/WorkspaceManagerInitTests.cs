using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Workspace;

public class WorkspaceManagerInitTests
{
    [Fact]
    public void Init_CreatesCentralStoreAndLeavesProjectClean()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);

        var result = manager.Init(null);

        result.WorkspaceName.ShouldBe(new DirectoryInfo(root).Name);
        result.RootPath.ShouldBe(Path.GetFullPath(root));

        File.Exists(Path.Combine(root, SqueezrPaths.ManifestFileName)).ShouldBeFalse();
        Directory.Exists(Path.Combine(root, SqueezrPaths.StoreDirName)).ShouldBeFalse();
        File.Exists(SqueezrPaths.ComposePath(root)).ShouldBeFalse();

        var storeDir = SqueezrPaths.StoreDir(root);
        Directory.Exists(storeDir).ShouldBeTrue();
        File.Exists(Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName)).ShouldBeTrue();
        File.Exists(SqueezrPaths.ConfigPath(root)).ShouldBeTrue();
        File.Exists(SqueezrPaths.MetaDbPath(root)).ShouldBeTrue();
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
    public void Init_DoesNotCreateStateFile()
    {
        var root = TestDirs.NewTempDir();
        new WorkspaceManager(root).Init(null);

        File.Exists(Path.Combine(SqueezrPaths.StoreDir(root), SqueezrPaths.StateFileName)).ShouldBeFalse();
    }

    [Fact]
    public void Init_WritesDefaultConfigMatchingSpec()
    {
        var root = TestDirs.NewTempDir();
        new WorkspaceManager(root).Init(null);

        var config = ConfigFile.Load(root);

        config.Embeddings.Provider.ShouldBe("auto");
        config.Embeddings.OpenAiCompatible.Endpoint.ShouldBe("https://api.openai.com/v1");
        config.Embeddings.OpenAiCompatible.ApiKeyEnv.ShouldBe("SQUEEZR_API_KEY");
        config.Embeddings.OpenAiCompatible.Model.ShouldBe("text-embedding-3-large");
        config.Embeddings.OpenAiCompatible.Dimensions.ShouldBe(1024);
        config.Chunking.MaxTokensPerChunk.ShouldBe(512);
        config.Chunking.FallbackWindowLines.ShouldBe(60);
        config.Chunking.FallbackOverlapLines.ShouldBe(15);
        string[] pinnedDefaults =
        [
            "**/node_modules/**",
            "**/bin/**",
            "**/obj/**",
            "**/dist/**",
            "**/.next/**",
            "**/.vs/**",
            "**/.idea/**",
            "**/.claude/worktrees/**",
            "**/.mindlash/worktrees/**",
            "**/.qwen/worktrees/**",
            "**/*.min.js",
            "**/*.min.mjs",
            "**/*.min.cjs",
            "**/*.tsbuildinfo",
            "**/web/out/**",
            "**/.qwen/tmp/**",
            "**/coverage/**",
            "**/.turbo/**",
            "**/.pytest_cache/**",
            "**/.gradle/**",
            "**/TestResults/**"
        ];
        config.Indexing.Ignore.ShouldBe(pinnedDefaults);
        config.Indexing.Ignore.ShouldBe(SqueezrConfig.CreateDefault().Indexing.Ignore);
        config.Indexing.Ignore.Count.ShouldBe(21);
        config.Indexing.Ignore[0].ShouldBe("**/node_modules/**");
        config.Indexing.Ignore[^1].ShouldBe("**/TestResults/**");
        config.Indexing.Ignore.ShouldNotContain("**/out/**");
        config.Indexing.RespectGitignore.ShouldBeTrue();
        config.Indexing.MaxFileSizeKb.ShouldBe(512);
        config.Qdrant.Endpoint.ShouldBe("http://localhost:6334");
    }

    [Fact]
    public void Init_DoesNotWriteComposeFile()
    {
        var root = TestDirs.NewTempDir();
        new WorkspaceManager(root).Init(null);

        File.Exists(SqueezrPaths.ComposePath(root)).ShouldBeFalse();
    }

    [Fact]
    public void Init_CreatedFiles_ListsStorePaths()
    {
        var root = TestDirs.NewTempDir();
        var result = new WorkspaceManager(root).Init(null);

        var storeDir = SqueezrPaths.StoreDir(root);
        result.CreatedFiles.ShouldBe(
        [
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            Path.Combine(storeDir, SqueezrPaths.ConfigFileName),
            Path.Combine(storeDir, SqueezrPaths.MetaDbFileName)
        ]);
        result.CreatedFiles.ShouldAllBe(file => File.Exists(file));
    }

    [Fact]
    public void Init_RegistersWorkspaceInRegistry()
    {
        var root = TestDirs.NewTempDir();
        var result = new WorkspaceManager(root).Init("my-system");

        var entry = WorkspaceRegistry.FindByRootPath(root);
        entry.ShouldNotBeNull();
        entry!.Name.ShouldBe("my-system");
        entry.RootPath.ShouldBe(Path.GetFullPath(root));
        entry.StoreKey.ShouldBe(WorkspaceStore.Resolve(root).StoreKey);
    }
}

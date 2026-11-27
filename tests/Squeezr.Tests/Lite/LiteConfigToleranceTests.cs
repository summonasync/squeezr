using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Lite.Config;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Lite;

public class LiteConfigToleranceTests
{
    [Fact]
    public void Load_FullProfileConfig_ParsesWithoutCrash_AndNamesIgnoredSections()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(SqueezrPaths.ConfigPath(root), FullProfileConfig);

        var config = LiteConfigFile.Load(root);

        config.Config.ShouldNotBeNull();
        config.Warnings.ShouldContain(warning => warning.Contains("'embeddings'", StringComparison.Ordinal));
        config.Warnings.ShouldContain(warning => warning.Contains("'qdrant'", StringComparison.Ordinal));
        config.Config.Indexing.MaxFileSizeKb.ShouldBe(512);
    }

    [Fact]
    public void Load_NullSemanticSections_ParsesWithoutCrash_AndWarnsOnce()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(SqueezrPaths.ConfigPath(root), """
            {
              "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": [], "maxFileSizeKb": 512, "rankExclude": [] },
              "embeddings": null,
              "qdrant": null
            }
            """);

        var config = LiteConfigFile.Load(root);

        config.Config.ShouldNotBeNull();
        config.Warnings.Count.ShouldBe(2);
        config.Warnings.ShouldAllBe(warning => warning.Contains("semantic profile", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_UnknownHandAddedSection_IsNamedInTheWarning()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(SqueezrPaths.ConfigPath(root), """
            {
              "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": [], "maxFileSizeKb": 512, "rankExclude": [] },
              "customSection": { "answer": 42 }
            }
            """);

        var config = LiteConfigFile.Load(root);

        config.Config.ShouldNotBeNull();
        config.Warnings.ShouldHaveSingleItem().ShouldContain("'customSection'");
        config.Warnings.Single().ShouldContain("not part of the lite schema");
    }

    [Fact]
    public void TryLoad_MissingConfigFile_AnswersMissingWithNoWarnings()
    {
        var config = LiteConfigFile.TryLoad(TestDirs.NewTempDir());

        config.Config.ShouldBeNull();
        config.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void RoundTrip_FullProfileConfigThroughCoreSave_KeepsSemanticSections()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(SqueezrPaths.ConfigPath(root), FullProfileConfig);

        var loaded = ConfigFile.Load(root);
        ConfigFile.Save(root, loaded);
        var reloaded = ConfigFile.Load(root);

        reloaded.ExtensionData.ShouldNotBeNull();
        reloaded.ExtensionData.Keys.ShouldBe(["embeddings", "qdrant"]);
        reloaded.Indexing.MaxFileSizeKb.ShouldBe(512);
        var embeddings = JsonSerializer.Deserialize<JsonElement>(reloaded.ExtensionData["embeddings"].GetRawText());
        embeddings.GetProperty("provider").GetString().ShouldBe("ollama");
    }

    [Fact]
    public void WorkspaceManagerInit_WritesConfigWithoutSemanticSections()
    {
        var root = TestDirs.NewTempDir();
        new WorkspaceManager(root).Init("demo");

        var json = File.ReadAllText(SqueezrPaths.ConfigPath(root));
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ShouldNotContain(name => name == "embeddings" || name == "qdrant");
    }

    private const string FullProfileConfig = """
        {
          "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
          "indexing": { "ignore": ["**/node_modules/**"], "maxFileSizeKb": 512, "noSemantic": false, "rankExclude": [] },
          "watch": { "enabled": false },
          "embeddings": {
            "provider": "ollama",
            "ollama": { "model": "nomic-embed-text", "endpoint": "http://localhost:11434" }
          },
          "qdrant": { "endpoint": "http://localhost:6334" }
        }
        """;
}

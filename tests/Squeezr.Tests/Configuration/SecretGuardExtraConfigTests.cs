using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Configuration;

public class SecretGuardExtraConfigTests
{
    [Fact]
    public void Load_ConfigWrittenBeforeTheKey_StillLoadsWithEmptyExtra()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [ "**/node_modules/**" ],
                "maxFileSizeKb": 512
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var config = SemanticConfigFile.Load(root);

        config.Indexing.SecretGuardExtra.ShouldBeEmpty();
        SecretGuard.IsProtected(".env", config.Indexing.SecretGuardExtra).ShouldBeTrue();
    }

    [Fact]
    public void Load_SecretGuardExtraGlobs_KeepOrderAndReachTheGuard()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [],
                "maxFileSizeKb": 512,
                "secretGuardExtra": [ "**/tokens.txt", "deploy/*.crt" ]
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var extra = SemanticConfigFile.Load(root).Indexing.SecretGuardExtra;

        extra.ShouldBe(["**/tokens.txt", "deploy/*.crt"]);
        SecretGuard.IsProtected("service/tokens.txt", extra).ShouldBeTrue();
        SecretGuard.IsProtected("deploy/server.crt", extra).ShouldBeTrue();
        SecretGuard.IsProtected("deploy/sub/deep.crt", extra).ShouldBeFalse();
        SecretGuard.IsProtected("src/notes.txt", extra).ShouldBeFalse();
    }

    [Fact]
    public void Load_SecretGuardExtraWithNullEntry_IsNotFatal()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [],
                "maxFileSizeKb": 512,
                "secretGuardExtra": [ null, "  ", "**/tokens.txt" ]
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var extra = SemanticConfigFile.Load(root).Indexing.SecretGuardExtra;

        SecretGuard.IsProtected("service/tokens.txt", extra).ShouldBeTrue();
        SecretGuard.IsProtected("service/readme.md", extra).ShouldBeFalse();
        SecretGuard.IsProtected(".env", extra).ShouldBeTrue();
    }

    [Fact]
    public void Load_SecretGuardExtraIsExplicitlyNull_GuardStillClassifies()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [],
                "maxFileSizeKb": 512,
                "secretGuardExtra": null
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var extra = SemanticConfigFile.Load(root).Indexing.SecretGuardExtra;

        extra.ShouldBeNull();
        SecretGuard.IsProtected(".env", extra).ShouldBeTrue();
        SecretGuard.IsProtected("deploy/prod.tokens", extra).ShouldBeFalse();
    }

    [Fact]
    public void Load_SameMaskInIgnoreAndInSecretGuardExtra_ProtectsAtTheDepthItExcludes()
    {
        var root = NewWorkspaceWithConfig(
            """
            {
              "chunking": {
                "maxTokensPerChunk": 512,
                "fallbackWindowLines": 60,
                "fallbackOverlapLines": 15
              },
              "indexing": {
                "ignore": [ "*.tokens" ],
                "maxFileSizeKb": 512,
                "secretGuardExtra": [ "*.tokens" ]
              },
              "qdrant": {
                "endpoint": "http://localhost:6334"
              }
            }
            """);

        var extra = SemanticConfigFile.Load(root).Indexing.SecretGuardExtra;

        SecretGuard.IsProtected("prod.tokens", extra).ShouldBeTrue();
        SecretGuard.IsProtected("deploy/deep.tokens", extra).ShouldBeTrue();
        SecretGuard.IsProtected("deploy/tokens.txt", extra).ShouldBeFalse();
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsSecretGuardExtra()
    {
        var root = TestDirs.NewTempDir();
        var config = SemanticConfig.Bind(SqueezrConfig.CreateDefault()) with
        {
            Indexing = SemanticConfig.Bind(SqueezrConfig.CreateDefault()).Indexing with
            {
                SecretGuardExtra = ["**/*.kdbx.userdata"]
            }
        };

        SemanticConfigFile.Save(root, config);
        var loaded = SemanticConfigFile.Load(root);

        loaded.Indexing.SecretGuardExtra.ShouldBe(["**/*.kdbx.userdata"]);
        SecretGuard.IsProtected("keep/vault.kdbx.userdata", loaded.Indexing.SecretGuardExtra).ShouldBeTrue();
    }

    [Fact]
    public void Save_EmptyExtra_WritesTheKeySoUsersCanFindIt()
    {
        var root = TestDirs.NewTempDir();

        SemanticConfigFile.Save(root, SemanticConfig.Bind(SqueezrConfig.CreateDefault()));

        File.ReadAllText(SqueezrPaths.ConfigPath(root)).ShouldContain("secretGuardExtra");
    }

    [Fact]
    public void BuiltInPatterns_AreNotPartOfTheEditableIgnoreDefaults()
    {
        var editable = SemanticConfig.Bind(SqueezrConfig.CreateDefault()).Indexing.Ignore;

        editable.ShouldNotBeEmpty();
        foreach (var pattern in SecretGuard.BuiltInPatterns)
        {
            editable.ShouldNotContain(pattern);
        }
    }

    private static string NewWorkspaceWithConfig(string json)
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(SqueezrPaths.ConfigPath(root), json);
        return root;
    }
}

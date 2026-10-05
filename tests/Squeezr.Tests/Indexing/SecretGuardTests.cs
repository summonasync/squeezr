using System.Text.RegularExpressions;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Ignore;

namespace Squeezr.Tests.Indexing;

public sealed partial class SecretGuardTests
{
    private static readonly string[] BuiltInSamples =
    [
        ".env",
        "web/.env.production",
        "deploy/tls/server.key",
        "a/b/c/monkey.pem",
        "certs/vault.pfx",
        "certs/old.p12",
        "droid/keystore.jks",
        "desktop/passwords.kdbx",
        ".ssh/id_rsa",
        ".ssh/id_ed25519",
        ".npmrc",
        ".netrc",
        ".git-credentials",
        ".pgpass",
        "secrets.json",
        "src/secrets.Staging.json",
        "app.secret"
    ];

    [Theory]
    [InlineData(".env")]
    [InlineData("config/.env")]
    [InlineData("deep/nested/.env")]
    [InlineData("web/.env.production")]
    [InlineData("a/b/c/.env.local")]
    [InlineData("deploy/server.key")]
    [InlineData("a/b/c/deep.key")]
    [InlineData("certs/chain.pem")]
    [InlineData("a/b/c/monkey.pem")]
    [InlineData("pfx/import.pfx")]
    [InlineData("pfx/import.p12")]
    [InlineData("droid/release.jks")]
    [InlineData("keep/vaults.kdbx")]
    [InlineData("id_rsa")]
    [InlineData(".ssh/id_rsa.pub")]
    [InlineData(".ssh/id_dsa")]
    [InlineData(".ssh/id_ecdsa_sk")]
    [InlineData(".ssh/id_ed25519.pub")]
    [InlineData(".npmrc")]
    [InlineData("home/.netrc")]
    [InlineData(".git-credentials")]
    [InlineData("var/.pgpass")]
    [InlineData("secrets.json")]
    [InlineData("src/secrets.Production.json")]
    [InlineData("tool/app.secret")]
    [InlineData("a/b/c/whatever.secret")]
    [InlineData("deploy\\tls\\server.key")]
    public void IsProtected_BuiltInMask_True(string relativePath)
    {
        SecretGuard.IsProtected(relativePath, []).ShouldBeTrue(relativePath);
    }

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("deploy/server.crt")]
    [InlineData("deploy/server.cer")]
    [InlineData("appsettings.Production.json")]
    [InlineData("keyring.cs")]
    [InlineData("keyboard.xml")]
    [InlineData("money.test.ts")]
    [InlineData("notes/secrets-hint.md")]
    [InlineData("keys/rotation-plan.md")]
    [InlineData("a/b/c/enviroment")]
    public void IsProtected_OutsideTheSet_False(string relativePath)
    {
        SecretGuard.IsProtected(relativePath, []).ShouldBeFalse(relativePath);
    }

    [Theory]
    [InlineData(".ENV")]
    [InlineData("CERTS/PRIVATE.PEM")]
    [InlineData("src/Secrets.JSON")]
    [InlineData(".SSH/ID_RSA")]
    [InlineData("App.Secret")]
    public void IsProtected_IgnoresCase(string relativePath)
    {
        SecretGuard.IsProtected(relativePath, []).ShouldBeTrue(relativePath);
    }

    [Theory]
    [InlineData(".env.example")]
    [InlineData(".env.sample")]
    [InlineData(".env.template")]
    [InlineData("web/.env.example")]
    [InlineData("deploy/config/.env.sample")]
    [InlineData("a/b/c/.env.template")]
    [InlineData("ENV.EXAMPLE")]
    public void IsProtected_EnvCarveOut_False(string relativePath)
    {
        SecretGuard.IsProtected(relativePath, []).ShouldBeFalse(relativePath);
    }

    [Theory]
    [InlineData(".env.existing")]
    [InlineData(".env.example.local")]
    [InlineData(".env.samples")]
    [InlineData("a/b/c/.env.existing")]
    public void IsProtected_EnvCarveOutIsExactName_NotAPrefix(string relativePath)
    {
        SecretGuard.IsProtected(relativePath, []).ShouldBeTrue(relativePath);
    }

    [Theory]
    [InlineData(".env.example")]
    [InlineData(".env.sample")]
    [InlineData(".env.template")]
    public void EnvCarveOut_NameShapedLikeAnotherMask_StillProtectedByThatMask(string name)
    {
        SecretGuard.BuiltInPatterns.Count(mask => GlobMatcher.IsMatch(mask, $"web/{name}")).ShouldBe(1);

        SecretGuard.IsProtected($"web/{name}.key", []).ShouldBeTrue(name);
    }

    [Fact]
    public void BuiltInPatterns_AreTheFrozenSpecList()
    {
        SecretGuard.BuiltInPatterns.ShouldBe(
        [
            "**/.env",
            "**/.env.*",
            "**/*.key",
            "**/*.pem",
            "**/*.pfx",
            "**/*.p12",
            "**/*.jks",
            "**/*.kdbx",
            "**/id_rsa*",
            "**/id_dsa*",
            "**/id_ecdsa*",
            "**/id_ed25519*",
            "**/.npmrc",
            "**/.netrc",
            "**/.git-credentials",
            "**/.pgpass",
            "**/secrets.json",
            "**/secrets.*.json",
            "**/*.secret"
        ]);
    }

    [Fact]
    public void BuiltInPatterns_IsNotAWindowIntoTheCodeList()
    {
        var reported = SecretGuard.BuiltInPatterns;

        (reported is string[]).ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => ((IList<string>)reported)[0] = "**/*");
        SecretGuard.BuiltInPatterns[0].ShouldBe("**/.env");
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("config/.env.production")]
    [InlineData("deploy/server.key")]
    [InlineData("certs/monkey.pem")]
    public void SqueezrignoreNegation_UnexcludesInRuleset_GuardStillProtects(string relativePath)
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(
            Path.Combine(root, ".squeezrignore"),
            "!**/.env\n!config/.env.production\n!**/*.key\n!**/*.pem\n");

        var config = SqueezrConfig.CreateDefault() with
        {
            Indexing = SqueezrConfig.CreateDefault().Indexing with
            {
                Ignore = [],
                RespectGitignore = false
            }
        };

        var decision = IgnoreRuleset.For(root, config).Decision(relativePath, false);
        decision.Excluded.ShouldBeFalse($"the ruleset is where '!' wins for {relativePath}");
        SecretGuard.IsProtected(relativePath, []).ShouldBeTrue(relativePath);
    }

    [Fact]
    public void RespectGitignoreDisabled_RulesetKeepsTheGitIgnoredSecret_GuardStillProtects()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, ".gitignore"), ".env\n*.key\n");

        var on = SqueezrConfig.CreateDefault();
        IgnoreRuleset.For(root, on).Decision(".env", false).Excluded.ShouldBeTrue();

        var off = on with { Indexing = on.Indexing with { RespectGitignore = false } };
        IgnoreRuleset.For(root, off).Decision(".env", false).Excluded.ShouldBeFalse();

        SecretGuard.IsProtected(".env", []).ShouldBeTrue();
        SecretGuard.IsProtected("deploy/server.key", []).ShouldBeTrue();
    }

    [Fact]
    public void EmptyIgnoreAndEmptyExtra_NarrowNothing()
    {
        var stripped = SqueezrConfig.CreateDefault().Indexing with
        {
            Ignore = [],
            SecretGuardExtra = [],
            RespectGitignore = false
        };

        foreach (var path in BuiltInSamples)
        {
            SecretGuard.IsProtected(path, stripped.SecretGuardExtra).ShouldBeTrue(path);
        }
    }

    [Fact]
    public void DefaultConfig_ExtraIsEmptyAndBuiltInsHold()
    {
        var indexing = SqueezrConfig.CreateDefault().Indexing;

        indexing.SecretGuardExtra.ShouldBeEmpty();

        foreach (var path in BuiltInSamples)
        {
            SecretGuard.IsProtected(path, indexing.SecretGuardExtra).ShouldBeTrue(path);
        }
    }

    [Theory]
    [InlineData("**/tokens.txt", "tokens.txt", true)]
    [InlineData("**/tokens.txt", "config/tokens.txt", true)]
    [InlineData("**/tokens.txt", "a/b/c/tokens.txt", true)]
    [InlineData("**/tokens.txt", "tokens.txt.bak", false)]
    [InlineData("deploy/vault pass/phrases.txt", "deploy/vault pass/phrases.txt", true)]
    [InlineData("*.tokens", "root.tokens", true)]
    [InlineData("*.tokens", "src/deep.tokens", true)]
    [InlineData("deploy/*.crt", "deploy/server.crt", true)]
    [InlineData("[p]rod.tokens", "prod.tokens", true)]
    [InlineData("deploy\\prod.tokens", "deployprod.tokens", true)]
    [InlineData("**/[Cc]redentials*", "config/Credentials.dev.json", true)]
    [InlineData("**/[Cc]redentials*", "config/credentials.json", true)]
    [InlineData("**/[Cc]redentials*", "config/tokens.txt", false)]
    [InlineData("Generated\\ Files/*.tokens", "Generated Files/vault.tokens", true)]
    [InlineData("Generated\\ Files/*.tokens", "GeneratedFiles/vault.tokens", false)]
    public void SecretGuardExtra_WidensTheSet(string extra, string relativePath, bool expected)
    {
        SecretGuard.IsProtected(relativePath, [extra]).ShouldBe(expected, $"{extra} vs {relativePath}");
    }

    // Union-only posture across the new dialect: a class or an escape may name more secrets, and a
    // negation still cannot name fewer — not even when it carries a class.
    [Fact]
    public void SecretGuardExtra_ClassesAndEscapesWidenOnly_NegationStaysInert()
    {
        var extras = new[] { "**/[Cc]redentials*", "!**/[Cc]redentials.sample.json" };

        SecretGuard.IsProtected("src/Credentials.prod.json", extras).ShouldBeTrue();
        SecretGuard.IsProtected("src/credentials.sample.json", extras).ShouldBeTrue();

        foreach (var path in BuiltInSamples)
        {
            SecretGuard.IsProtected(path, extras).ShouldBeTrue($"{path} under class extras");
        }
    }

    [Theory]
    [InlineData("*.tokens", "root.tokens", true)]
    [InlineData("*.tokens", "src/deep.tokens", true)]
    [InlineData("*.tokens", "src/readme.md", false)]
    [InlineData("prod-secrets.yaml", "deploy/prod-secrets.yaml", true)]
    [InlineData("deploy/*.crt", "deploy/server.crt", true)]
    [InlineData("deploy/*.crt", "deploy/sub/deep.crt", false)]
    [InlineData("**/tokens.txt", "a/b/c/tokens.txt", true)]
    [InlineData("**/tokens.txt", "a/b/c/tokens.txt.bak", false)]
    [InlineData("/phrases.custom", "phrases.custom", true)]
    [InlineData("/phrases.custom", "src/phrases.custom", false)]
    [InlineData("secrets/", "secrets", false)]
    [InlineData("[p]rod.tokens", "prod.tokens", true)]
    [InlineData("**/[Cc]redentials*", "config/Credentials.dev.json", true)]
    [InlineData("Generated\\ Files/*.tokens", "Generated Files/vault.tokens", true)]
    [InlineData("Generated\\ Files/*.tokens", "GeneratedFiles/vault.tokens", false)]
    public void SecretGuardExtra_MeansWhatIndexingIgnoreMeans(string extra, string relativePath, bool expected)
    {
        var root = TestDirs.NewTempDir();
        var config = SqueezrConfig.CreateDefault() with
        {
            Indexing = SqueezrConfig.CreateDefault().Indexing with
            {
                Ignore = [extra],
                RespectGitignore = false
            }
        };

        IgnoreRuleset.For(root, config).Decision(relativePath, false).Excluded
            .ShouldBe(expected, $"indexing.ignore '{extra}' vs {relativePath}");
        SecretGuard.IsProtected(relativePath, [extra]).ShouldBe(expected, $"secretGuardExtra '{extra}' vs {relativePath}");
    }

    [Theory]
    [InlineData("  **/tokens.txt  ")]
    [InlineData("\t**/tokens.txt")]
    [InlineData("**/tokens.txt\n")]
    public void SecretGuardExtra_SurroundingWhitespace_StillApplies(string extra)
    {
        SecretGuard.IsProtected("config/tokens.txt", [extra]).ShouldBeTrue(extra);
    }

    [Theory]
    [InlineData("!**/.env", "prod.tokens")]
    [InlineData("!prod.tokens", "prod.tokens")]
    [InlineData("!!.env", "prod.tokens")]
    [InlineData("!!.prod.tokens", "prod.tokens")]
    [InlineData("#prod.tokens", "prod.tokens")]
    [InlineData("secrets/", "secrets")]
    [InlineData("[[:alpha:]]prod.tokens", "prod.tokens")]
    [InlineData("a\\/prod.tokens", "a/prod.tokens")]
    [InlineData("[unclosed.tokens", "unclosed.tokens")]
    [InlineData("dangling\\", "dangling")]
    [InlineData(" ", "prod.tokens")]
    [InlineData("", "prod.tokens")]
    [InlineData(null, "prod.tokens")]
    public void SecretGuardExtra_CannotNameAFileShape_NeitherWidensNorNarrows(string? extra, string relativePath)
    {
        var extras = new[] { extra! };

        SecretGuard.IsProtected(relativePath, extras).ShouldBeFalse($"{extra} vs {relativePath}");
        SecretGuard.IsProtected("web/.env.example", extras).ShouldBeFalse(extra);

        foreach (var path in BuiltInSamples)
        {
            SecretGuard.IsProtected(path, extras).ShouldBeTrue($"{extra} vs {path}");
        }
    }

    [Fact]
    public void SecretGuardExtra_CarveOutCanBeReopenedByTheAuthor()
    {
        SecretGuard.IsProtected("web/.env.example", ["**/.env.example"]).ShouldBeTrue();
        SecretGuard.IsProtected("web/.env.example", []).ShouldBeFalse();
    }

    [Fact]
    public void SecretGuardExtra_MalformedEntriesLeaveTheBuiltInsAlone()
    {
        SecretGuard.IsProtected(".env", null!).ShouldBeTrue();
        SecretGuard.IsProtected(".env", ["", "   ", null!]).ShouldBeTrue();
        SecretGuard.IsProtected("keep/vaults.kdbx", [" ", "**/tokens.txt"]).ShouldBeTrue();
        SecretGuard.IsProtected("src/App.cs", ["", "  "]).ShouldBeFalse();
    }

    [Theory]
    [InlineData(".env/dev")]
    [InlineData(".env/local.json")]
    [InlineData("deploy/secrets/prod.json")]
    [InlineData("vault/token.key/readme.md")]
    public void DirectoryShapedSecret_LastSegmentDecides_NotProtected(string relativePath)
    {
        SecretGuard.IsProtected(relativePath, []).ShouldBeFalse(relativePath);
    }

    [Fact]
    public void DirectoryShapedSecret_FilesInsideIt_AreCoveredOnlyByNameShapedMasks()
    {
        SecretGuard.IsProtected(".env", []).ShouldBeTrue();
        SecretGuard.IsProtected(".env/dev", []).ShouldBeFalse();
        SecretGuard.IsProtected(".env/prod.key", []).ShouldBeTrue();
        SecretGuard.IsProtected("secrets/secrets.json", []).ShouldBeTrue();
    }

    [Fact]
    public void IsProtected_NullPath_ThrowsInsteadOfCallingItSafe()
    {
        Should.Throw<ArgumentNullException>(() => SecretGuard.IsProtected(null!, []));
    }

    [Fact]
    public void IsProtected_PathThatDoesNotExistOnDisk_StillClassifies()
    {
        var missing = $"no/such/dir-{Guid.NewGuid():N}";

        SecretGuard.IsProtected($"{missing}/.env", []).ShouldBeTrue();
        SecretGuard.IsProtected($"{missing}/keystore.jks", []).ShouldBeTrue();
        SecretGuard.IsProtected($"{missing}/readme.md", []).ShouldBeFalse();
    }

    [Fact]
    public void Classifier_SourceHasNoFileSystemProbe()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Squeezr.Core", "Indexing", "SecretGuard.cs"));

        FileSystemProbe().IsMatch(source).ShouldBeFalse();
    }

    [Fact]
    public void ProbeIsNotBlind_ToTheCallItBans()
    {
        foreach (var probe in new[]
        {
            "File.Exists(path)",
            "Directory.GetFiles(dir)",
            "new FileStream(path, FileMode.Open)",
            "RandomAccess.Read(handle, span, 0)",
            "new FileInfo(name).Length"
        })
        {
            FileSystemProbe().IsMatch(probe).ShouldBeTrue(probe);
        }

        FileSystemProbe().IsMatch("relativePath.Replace('\\\\', '/')").ShouldBeFalse();
    }

    [GeneratedRegex(@"(Directory|File|FileSystemInfo|RandomAccess)\.\w+\(|new (DirectoryInfo|FileInfo|FileStream|StreamWriter)\(")]
    private static partial Regex FileSystemProbe();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Repository root (Squeezr.slnx) not found above {AppContext.BaseDirectory}; " +
            "the secret guard cannot be audited outside a checkout.");
    }
}

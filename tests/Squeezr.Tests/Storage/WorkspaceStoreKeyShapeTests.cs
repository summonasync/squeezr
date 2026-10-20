using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

// The removal paths resolve a store dir through RequireStoreDir, which refuses anything that is not one
// segment. Legacy migration feeds keys that WorkspaceStoreKeys.Create just minted, so the throw there has
// to be unreachable by construction: the tail is always eight hex digits, never a separator or a colon.
public sealed class WorkspaceStoreKeyShapeTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:evil")]
    [InlineData("..:stream")]
    [InlineData("-leading")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("a b")]
    [InlineData("проект")]
    [InlineData("café-work")]
    [InlineData("דגם")]
    [InlineData("name with many separators - and - dashes - and.dots")]
    public void Create_NamesExactlyOneSegmentUnderWorkspaces(string workspaceName)
    {
        var storeKey = WorkspaceStoreKeys.Create(workspaceName, $"/tmp/squeezr-key-shape-{Guid.NewGuid():N}");

        WorkspaceStorePaths.IsSingleSegmentKey(storeKey).ShouldBeTrue();
        Path.GetFileName(WorkspaceStorePaths.RequireStoreDir(storeKey)).ShouldBe(storeKey);
        storeKey.Contains('/').ShouldBeFalse();
        storeKey.Contains('\\').ShouldBeFalse();
        storeKey.Contains(':').ShouldBeFalse();
        storeKey.Contains('\0').ShouldBeFalse();
        storeKey.ShouldNotBe(".");
        storeKey.ShouldNotBe("..");

        var tail = storeKey[(storeKey.LastIndexOf('-') + 1)..];
        tail.Length.ShouldBe(8);
        tail.All(Uri.IsHexDigit).ShouldBeTrue();
        Uri.IsHexDigit(storeKey[^1]).ShouldBeTrue();
    }

    [Fact]
    public void Slug_KeepsLettersAndDigits_AndTurnsEverythingElseIntoASeparator()
    {
        WorkspaceStoreKeys.Slug("Proj/sub\\dir:name").ShouldBe("proj-sub-dir-name");
        WorkspaceStoreKeys.Slug("  ").ShouldBe("workspace");
    }

    // Registry hygiene warns and drops by storeKey, so the derivation itself is a contract:
    // slug(name) + "-" + first eight hex digits of sha1(canonicalRoot). Any change here renames
    // every row in every registry and orphans every store dir on disk.
    [Fact]
    public void Create_Derivation_IsSlugPlusFirstEightSha1HexDigitsOfTheCanonicalRoot()
    {
        var root = "/tmp/squeezr-key-derivation";
        var expectedTail = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(root)))[..8].ToLowerInvariant();

        WorkspaceStoreKeys.Create("Demo Repo", root).ShouldBe($"demo-repo-{expectedTail}");
    }
}

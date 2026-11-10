using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;

namespace Squeezr.Tests.Workspace;

public class WorktreeFactsTests
{
    [Fact]
    public void Resolve_PrimaryWorktree_ReadsBranchAndKeepsGitStateInItsOwnDir()
    {
        var main = SeededMainRepo();

        var facts = WorktreeFacts.Resolve(Path.Combine(main, "seed.txt")).ShouldNotBeNull();

        facts.Toplevel.ShouldBe(main);
        facts.GitDir.ShouldBe(Path.Combine(main, ".git"));
        facts.CommonDir.ShouldBe(facts.GitDir);
        facts.IsLinked.ShouldBeFalse();
        facts.IsDetached.ShouldBeFalse();
        var branch = facts.Branch.ShouldNotBeNull("HEAD points at a branch, so the branch is knowable");
        facts.MainWorktreeRoot.ShouldBe(main);
        facts.StoreName.ShouldBe(WorkspaceStoreKeys.Slug(new DirectoryInfo(main).Name),
            "a primary checkout is named after its folder, which is the name init registered under");
        facts.StoreName.ShouldNotBe(WorkspaceStoreKeys.Slug(branch),
            "the branch is not the parent's name — a second spelling forks one tree into two identities");
    }

    [Fact]
    public void Resolve_LinkedWorktree_ReportsItsOwnToplevelAndTheMainsRoot()
    {
        var main = SeededMainRepo();
        var worktree = LinkedWorktree(main, "repo", "feat/111-rust-language-support");
        File.WriteAllText(Path.Combine(worktree, "notes.md"), "notes\n");

        var facts = WorktreeFacts.Resolve(Path.Combine(worktree, "notes.md")).ShouldNotBeNull();

        facts.Toplevel.ShouldBe(worktree);
        facts.GitDir.ShouldBe(Path.Combine(main, ".git", "worktrees", "repo"));
        facts.CommonDir.ShouldBe(Path.Combine(main, ".git"));
        facts.IsLinked.ShouldBeTrue();
        facts.Branch.ShouldBe("feat/111-rust-language-support");
        facts.IsDetached.ShouldBeFalse();
        facts.MainWorktreeRoot.ShouldBe(main);
    }

    [Fact]
    public void Resolve_FeatureBranch_StoreNameIsTheSluggedLeafNotTheRefNamespace()
    {
        var main = SeededMainRepo();
        var worktree = LinkedWorktree(main, "wk", "feat/111-rust-language-support");

        var facts = WorktreeFacts.Resolve(worktree).ShouldNotBeNull();

        var branch = facts.Branch.ShouldNotBeNull();
        facts.StoreName.ShouldBe("111-rust-language-support");
        facts.StoreName.ShouldBe(WorkspaceStoreKeys.Slug(branch.Split('/')[^1]));
        facts.StoreName.ShouldNotBe("wk", "a linked tree borrows the branch leaf, not the folder it sits in");
    }

    [Fact]
    public void Resolve_BranchLeafNeedingCleanup_SlugsTheLeafNotTheRawRef()
    {
        var main = SeededMainRepo();
        var worktree = LinkedWorktree(main, "facts", "feat/116_facts.isolated");

        var facts = WorktreeFacts.Resolve(worktree).ShouldNotBeNull();

        facts.Branch.ShouldBe("feat/116_facts.isolated");
        facts.StoreName.ShouldBe("116-facts-isolated");
    }

    [Fact]
    public void Resolve_DetachedHead_HasNoBranchAndNamesItselfAfterTheWorktreeDir()
    {
        var main = SeededMainRepo();
        var worktree = LinkedWorktree(main, "116-detached", "detached-lane");
        TestGitRepo.Detach(worktree);

        var facts = WorktreeFacts.Resolve(worktree).ShouldNotBeNull();

        facts.IsDetached.ShouldBeTrue();
        facts.Branch.ShouldBeNull();
        facts.IsLinked.ShouldBeTrue();
        facts.MainWorktreeRoot.ShouldBe(main);
        facts.StoreName.ShouldBe("116-detached");
    }

    [Fact]
    public void Resolve_HeadNamesRefOutsideRefsHeads_IsUnknownNotDetached()
    {
        var main = SeededMainRepo();
        var worktree = LinkedWorktree(main, "oddleaf", "feat/900-oddref");
        TestGitRepo.SymbolicRef(worktree, "refs/remotes/origin/main");

        var facts = WorktreeFacts.Resolve(worktree).ShouldNotBeNull();

        facts.Branch.ShouldBeNull("a ref outside refs/heads names no branch the product can claim");
        facts.IsDetached.ShouldBeFalse("an odd ref is still a symbolic HEAD, not a checkout of a bare sha");
        facts.StoreName.ShouldBe("oddleaf", "no branch means the worktree dir names it");
    }

    [Fact]
    public void Resolve_TruncatedHeadSha_IsUnknownNotDetached()
    {
        var main = SeededMainRepo();
        var worktree = LinkedWorktree(main, "cutleaf", "feat/901-cut");
        TestGitRepo.Detach(worktree);
        var head = Path.Combine(main, ".git", "worktrees", "cutleaf", "HEAD");
        var sha = File.ReadAllText(head).Trim();
        (sha.Length is 40 or 64).ShouldBeTrue("git writes a whole lowercase sha here; the reader trusts only those");
        File.WriteAllText(head, sha[..12] + "\n");

        var facts = WorktreeFacts.Resolve(worktree).ShouldNotBeNull();

        facts.IsDetached.ShouldBeFalse("half a sha is a partial write, not a detached checkout");
        facts.Branch.ShouldBeNull();
        facts.StoreName.ShouldBe("cutleaf");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Resolve_UnreadableHead_KeepsTreeFactsAndClaimsNoBranch()
    {
        var main = SeededMainRepo();
        var worktree = LinkedWorktree(main, "lockleaf", "feat/902-lock");
        var head = Path.Combine(main, ".git", "worktrees", "lockleaf", "HEAD");
        var originalMode = File.GetUnixFileMode(head);
        File.SetUnixFileMode(head, UnixFileMode.None);
        try
        {
            Should.Throw<UnauthorizedAccessException>(() => File.ReadAllText(head));

            var facts = WorktreeFacts.Resolve(worktree).ShouldNotBeNull();

            facts.Branch.ShouldBeNull();
            facts.IsDetached.ShouldBeFalse();
            facts.IsLinked.ShouldBeTrue();
            facts.MainWorktreeRoot.ShouldBe(main);
            facts.StoreName.ShouldBe("lockleaf");
        }
        finally
        {
            File.SetUnixFileMode(head, originalMode);
        }
    }

    [Fact]
    public void Resolve_PlainDirectory_ReturnsNull()
    {
        var plain = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(plain, "readme.md"), "no git here\n");

        WorktreeFacts.Resolve(plain).ShouldBeNull();
        WorktreeFacts.Resolve(Path.Combine(plain, "readme.md")).ShouldBeNull();
        WorktreeFacts.Resolve(Path.Combine(plain, "sub", "deep")).ShouldBeNull();
    }

    [Fact]
    public void Resolve_BlankPath_ReturnsNullWithoutThrowing()
    {
        WorktreeFacts.Resolve("").ShouldBeNull("a blank argument degrades like GitDirLink, it is not an ArgumentException");
        WorktreeFacts.Resolve(" ").ShouldBeNull();
        WorktreeFacts.Resolve("\t ").ShouldBeNull();
        WorktreeFacts.Resolve("a\0b").ShouldBeNull("a NUL can arrive through a JSON argument on the MCP path, not only argv");
    }

    [Fact]
    public void Resolve_DirectoryNamedLikeALaneButNotGit_ReturnsNull()
    {
        var root = TestDirs.NewTempDir();
        var lane = Path.Combine(root, "feat", "116-worktree-aware-workspaces");
        Directory.CreateDirectory(lane);

        WorktreeFacts.Resolve(lane).ShouldBeNull("a folder name says nothing, git files say everything");
    }

    [Fact]
    public void Resolve_DanglingGitDirPointer_ReturnsNullInsteadOfHalfFacts()
    {
        var husk = TestDirs.NewTempDir();
        var worktree = Path.Combine(husk, "repo");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /nonexistent/worktrees/repo\n");

        var facts = WorktreeFacts.Resolve(worktree);

        facts.ShouldBeNull("an unresolvable pointer hides branch, common dir and parent, so nothing is claimed");
    }

    [Fact]
    public void Resolve_CommonDirSpelledOutsideAWorktree_LeavesTheMainRootUnknown()
    {
        var root = TestDirs.NewTempDir();
        var common = Path.Combine(root, "elsewhere", "repo.git");
        var admin = Path.Combine(root, "gadm", "wk");
        var worktree = Path.Combine(root, "wk");
        Directory.CreateDirectory(common);
        Directory.CreateDirectory(admin);
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {admin}\n");
        File.WriteAllText(Path.Combine(admin, "commondir"), "../../elsewhere/repo.git\n");
        File.WriteAllText(Path.Combine(admin, "HEAD"), "ref: refs/heads/main\n");

        var facts = WorktreeFacts.Resolve(worktree).ShouldNotBeNull();

        facts.Toplevel.ShouldBe(worktree);
        facts.CommonDir.ShouldBe(common);
        facts.IsLinked.ShouldBeTrue();
        facts.Branch.ShouldBe("main");
        facts.MainWorktreeRoot.ShouldBeNull("a common dir git did not lay out records no main worktree root");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Resolve_UnreadableCommonDirFile_ReturnsNullInsteadOfHalfFacts()
    {
        var main = SeededMainRepo();
        var worktree = LinkedWorktree(main, "lockdir", "feat/903-lockdir");
        var commondir = Path.Combine(main, ".git", "worktrees", "lockdir", "commondir");
        var originalMode = File.GetUnixFileMode(commondir);
        File.SetUnixFileMode(commondir, UnixFileMode.None);
        try
        {
            Should.Throw<UnauthorizedAccessException>(() => File.ReadAllText(commondir));

            WorktreeFacts.Resolve(worktree).ShouldBeNull(
                "linked-ness and the parent both come from commondir, so unreadable means nothing is claimed");
        }
        finally
        {
            File.SetUnixFileMode(commondir, originalMode);
        }
    }

    [Fact]
    public void Resolve_TrailingSeparatorOnThePath_ToplevelsTheSameDirectory()
    {
        var main = SeededMainRepo();
        var worktree = LinkedWorktree(main, "repo", "lane");

        var facts = WorktreeFacts.Resolve(worktree + Path.DirectorySeparatorChar).ShouldNotBeNull();

        facts.Toplevel.ShouldBe(worktree);
        facts.MainWorktreeRoot.ShouldBe(main);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Resolve_SymlinkedSpelling_ReportsOneIdentityForOneTree()
    {
        var main = SeededMainRepo();
        var link = Path.Combine(TestDirs.NewTempDir(), "link-to-main");
        Directory.CreateSymbolicLink(link, main);

        var viaLink = WorktreeFacts.Resolve(link).ShouldNotBeNull();
        var viaReal = WorktreeFacts.Resolve(main).ShouldNotBeNull();

        viaLink.Toplevel.ShouldBe(viaReal.Toplevel,
            "one tree is one toplevel whichever spelling the caller walked in through");
        viaLink.StoreName.ShouldBe(viaReal.StoreName,
            "a symlinked path must not mint a second store identity for the same checkout");
        viaLink.GitDir.ShouldBe(viaReal.GitDir);
        viaLink.MainWorktreeRoot.ShouldBe(viaReal.MainWorktreeRoot);
    }

    private static string SeededMainRepo()
    {
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        return main;
    }

    private static string LinkedWorktree(string main, string leaf, string branch)
    {
        var worktree = Path.Combine(TestDirs.NewTempDir(), leaf);
        TestGitRepo.AddWorktree(main, worktree, branch);
        return worktree;
    }
}

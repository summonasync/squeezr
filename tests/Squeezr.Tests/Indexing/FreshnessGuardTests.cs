using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public class FreshnessGuardTests
{
    [Fact]
    public void UnchangedFile_LeavesIndexUntouched()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"stableneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        var sizeBefore = new FileInfo(SqueezrPaths.TrigramIndexPath(workspace)).Length;
        var indexedAtBefore = IndexedAt(workspace, "a.cs");

        var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);

        result.Refreshed.ShouldBeEmpty();
        result.Stale.ShouldBeEmpty();
        new FileInfo(SqueezrPaths.TrigramIndexPath(workspace)).Length.ShouldBe(sizeBefore);
        IndexedAt(workspace, "a.cs").ShouldBe(indexedAtBefore);
    }

    [Fact]
    public void TouchedFile_SameContent_ServedFromIndexWithoutReindex()
    {
        var workspace = NewWorkspace();
        var fullPath = Path.Combine(workspace, "repo", "a.cs");
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"touchneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        var indexedAtBefore = IndexedAt(workspace, "a.cs");
        File.SetLastWriteTimeUtc(fullPath, DateTime.UtcNow.AddMinutes(5));

        var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);

        result.Refreshed.ShouldBeEmpty();
        result.Stale.ShouldBeEmpty();
        IndexedAt(workspace, "a.cs").ShouldBe(indexedAtBefore);
    }

    [Fact]
    public void ModifiedFile_IsRefreshedAndNewContentIsSearchable()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"oldneedle\"; }");
        new WorkspaceIndexer().Index(workspace);

        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"freshneedle\"; }");
        var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);

        result.Refreshed.ShouldBe(["demo/a.cs"]);
        result.Stale.ShouldBeEmpty();

        var engine = new SearchEngine(workspace);
        engine.Search(Query("oldneedle")).Matches.ShouldBeEmpty();
        engine.Search(Query("freshneedle")).Matches.ShouldHaveSingleItem().Path.ShouldBe("a.cs");
    }

    [Fact]
    public void DeletedFile_IsRemovedFromIndex()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"goneneedle\"; }");
        WriteFile(workspace, "repo/b.cs", "class Beta { }");
        new WorkspaceIndexer().Index(workspace);

        File.Delete(Path.Combine(workspace, "repo", "a.cs"));
        var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);

        result.Refreshed.ShouldBe(["demo/a.cs"]);
        result.Stale.ShouldBeEmpty();
        new SearchEngine(workspace).Search(Query("goneneedle")).Matches.ShouldBeEmpty();
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        store.ListFiles(repoDoc.Id).ShouldHaveSingleItem().Path.ShouldBe("b.cs");
    }

    [Fact]
    public void LockedIndex_FileMarkedStale_NoException()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"lockneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"changedneedle\"; }");

        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspace)))
        {
            var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);

            result.Refreshed.ShouldBeEmpty();
            result.Stale.ShouldBe(["demo/a.cs"]);
        }

        var after = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);
        after.Refreshed.ShouldBe(["demo/a.cs"]);
        after.Stale.ShouldBeEmpty();
    }

    [Fact]
    public void DeletedFile_WithLockHeld_MarkedStale_IndexUntouched()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"lockedgone\"; }");
        new WorkspaceIndexer().Index(workspace);
        File.Delete(Path.Combine(workspace, "repo", "a.cs"));

        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspace)))
        {
            var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);

            result.Refreshed.ShouldBeEmpty();
            result.Stale.ShouldBe(["demo/a.cs"]);
        }

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        store.FindRepo("demo")!.ShouldNotBeNull();
        store.ListFiles(store.FindRepo("demo")!.Id).ShouldHaveSingleItem().Path.ShouldBe("a.cs");
    }

    [Fact]
    public void CandidateMissingFromIndex_GetsIndexed()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { }");
        new WorkspaceIndexer().Index(workspace);
        WriteFile(workspace, "repo/new.cs", "class Fresh { string Term = \"newneedle\"; }");

        var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "new.cs")]);

        result.Refreshed.ShouldBe(["demo/new.cs"]);
        result.Stale.ShouldBeEmpty();
        new SearchEngine(workspace).Search(Query("newneedle")).Matches.ShouldHaveSingleItem().Path.ShouldBe("new.cs");
    }

    [Fact]
    public void CancelledCall_BeforeAnyReRead_TurnsEveryCandidateStale_WithoutReIndexing()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"cancelneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"qwzzvkjw\"; }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")], cancellationToken: cts.Token);

        result.Refreshed.ShouldBeEmpty();
        result.Stale.ShouldBe(["demo/a.cs"]);
        result.Unrefreshed.ShouldBeEmpty();
        new SearchEngine(workspace).Search(Query("qwzzvkjw")).Matches.ShouldBeEmpty(
            "the row a re-read would have written is not there — nothing was indexed behind the cancellation");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task IndexedEmptyFile_PathSubstitutedWithAWriterlessFifo_DoesNotBlockTheFreshnessProbe()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/empty.cs", string.Empty);
        new WorkspaceIndexer().Index(workspace);

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var repoDoc = store.FindRepo("demo")!;
            store.ListFiles(repoDoc.Id)
                .Single(f => f.Path == "empty.cs")
                .SizeBytes.ShouldBe(0, "a channel imitates an empty row only because the row really is empty");
        }

        var fifo = Path.Combine(workspace, "repo", "empty.cs");
        File.Delete(fifo);
        Assert.Equal(0, mkfifo(fifo, 0b100_100_100));

        // asserted before the call so a regressed stat probe fails fast instead of hanging here
        UnixFileType.IsRegularFile(fifo).ShouldBeFalse();

        var call = Task.Run(() => FreshnessGuard.CheckAndRefresh(workspace, [("demo", "empty.cs")]));
        (await Task.WhenAny(call, Task.Delay(WedgeBudget))).ShouldBe(call, $"the freshness probe opened {fifo}");

        var result = await call;
        result.Refreshed.ShouldBe(["demo/empty.cs"]);
        result.Stale.ShouldBeEmpty();
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var repoDoc = store.FindRepo("demo")!;
            store.ListFiles(repoDoc.Id).ShouldNotContain(f => f.Path == "empty.cs",
                "a channel carries no indexable content, so the row has to leave with the open that never happened");
        }
    }

    [Fact]
    public void ProtectedPath_WithContentNewerThanTheRow_IsSkippedWithoutAReRead()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithSecrets();
        new WorkspaceIndexer().Index(workspace);
        SecretGuardFixture.PlantPreGuardRows(workspace, [IngestionWorkspaces.EnvPath]);
        var storedSha = StoredSha(workspace, IngestionWorkspaces.EnvPath);
        var withheld = IngestionWorkspaces.LedgerWithheld(workspace);

        File.AppendAllText(
            IngestionWorkspaces.Full(workspace, IngestionWorkspaces.EnvPath), "SUF=POSTGUARDOVEDITNEEDLE\n");

        var result = FreshnessGuard.CheckAndRefresh(workspace,
            [("demo", IngestionWorkspaces.EnvPath), ("demo", IngestionWorkspaces.AppPath)]);

        result.Refreshed.ShouldBeEmpty();
        result.Stale.ShouldBeEmpty();
        StoredSha(workspace, IngestionWorkspaces.EnvPath).ShouldBe(storedSha,
            "the row kept the hash of the content the guard never opened");
        IngestionWorkspaces.LedgerWithheld(workspace).ShouldBe(withheld,
            "a re-read would reach ingestion and be counted there as a refusal");
        new SearchEngine(workspace).Search(Query("POSTGUARDOVEDITNEEDLE")).Matches.ShouldBeEmpty();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void IndexedEmptyFile_PathSubstitutedWithAUnixSocket_TakesTheRefreshRouteNotTheHashRead()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/empty.cs", string.Empty);
        new WorkspaceIndexer().Index(workspace);

        var path = Path.Combine(workspace, "repo", "empty.cs");
        File.Delete(path);
        // the listener stays open for the whole test: closing it unlinks the socket file, and a stale
        // socket inode is what File.Exists already refuses — the live one is the shape that reaches the probe
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));

        // the premises are what make this a value assertion rather than the timing one the FIFO test is:
        // a socket is present for stat and reads fail at once, so a dropped file-type probe shows up as a
        // stale verdict instead of as the hang the writerless channel would cause
        File.Exists(path).ShouldBeTrue("a socket is present for File.Exists, so only the type probe stands between it and the read");
        UnixFileType.IsRegularFile(path).ShouldBeFalse();
        Should.Throw<IOException>(() => File.ReadAllBytes(path));

        var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "empty.cs")]);

        result.Refreshed.ShouldBe(["demo/empty.cs"]);
        result.Stale.ShouldBeEmpty();
    }

    [Fact]
    public void GuardSource_ReadsTheFileRowByIndex_AndOpensOneStoreSessionPerCall()
    {
        var source = File.ReadAllText(
            Path.Combine(SolutionRoot(), "src/Squeezr.Core/Indexing/FreshnessGuard.cs"));

        CountOccurrences(source, "new MetaStore(").ShouldBe(1,
            "one session per call — a second open site is the shape that made a candidate pay a store roundtrip");
        source.ShouldNotContain("ListFiles(",
            customMessage: "the files collection has an index on path; scanning every repo row per candidate is the price this route replaced");
        source.ShouldContain("FindFile(",
            customMessage: "the row a candidate names is looked up, not filtered out of a scan");

        var loop = CandidateLoopBody(source);
        loop.ShouldNotContain("new MetaStore(",
            customMessage: "the candidate loop must not open the store");
        loop.ShouldNotContain("WorkspaceManifestFile.Load(",
            customMessage: "the manifest is read once for the call, not once per candidate");
        loop.ShouldNotContain("SecretGuardIngestion.Load(",
            customMessage: "the guard profile is read once for the call, not once per candidate");
    }

    // the token the guard reads at its budget point is only as real as the wire that feeds it: each tool's
    // gate lambda hands ct into its Core method, the Core method passes it at the Guard call, and the Guard
    // helper forwards it to CheckAndRefresh — a default at any of the three hops reads as no cancellation
    [Fact]
    public void InGateCallers_ThreadTheGateToken_IntoTheGuard()
    {
        var wired = new (string Tool, string GateCall)[]
        {
            ("CodeSearchTool.cs", "SearchCore(query, repo, prefix, maxTokens, topK, kind, ct)"),
            ("SymbolLookupTool.cs", "FindCore(name, symbol, kind, repo, maxTokens, ct)"),
            ("FindReferencesTool.cs", "FindCore(symbol, name, repo, ct)"),
        };

        foreach (var (tool, gateCall) in wired)
        {
            var source = File.ReadAllText(Path.Combine(SolutionRoot(), "src/Squeezr.Cli/Mcp/Tools", tool));
            source.ShouldContain(gateCall,
                customMessage: $"{tool} must hand the gate's ct into its Core method — the only road the guard's token has");
            source.ShouldContain("Guard(root, candidates, cancellationToken)",
                customMessage: $"{tool} must pass that token at the guard call, not default");
            source.ShouldContain(
                "FreshnessGuard.CheckAndRefresh(root, candidates, StaleLockWarningSink.ToStderr, cancellationToken)",
                customMessage: $"{tool}'s Guard helper must forward the token to CheckAndRefresh");
        }
    }

    [Fact]
    public void MissingStore_EveryNonProtectedCandidateTurnsStale_WithoutAThrow()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithSecrets();
        new WorkspaceIndexer().Index(workspace);
        File.Delete(SqueezrPaths.MetaDbPath(workspace));

        FreshnessResult result = default;
        Should.NotThrow(() => result = FreshnessGuard.CheckAndRefresh(workspace,
            [("demo", IngestionWorkspaces.AppPath), ("demo", IngestionWorkspaces.EnvPath)]));

        // the per-candidate open used to fail one candidate at a time into the same list; the shared
        // session fails once, so this pins that the tally did not move — and that the protected path is
        // still not counted, which is the ordering the old loop gave by skipping it before the probe
        result.Refreshed.ShouldBeEmpty();
        result.Stale.ShouldBe(["demo/src/App.cs"], StringComparer.Ordinal);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void RowThatDiffersFromTheCandidateOnlyInCase_IsNotAcceptedAsTheCandidatesRow()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/Case.cs", "class Cased { string Term = \"ordinalneedle\"; }\n");
        new WorkspaceIndexer().Index(workspace);

        // a real second file, identical in content, differing from the indexed row only by one letter's
        // case: the path index answers with the other row, so only an Ordinal check on the row keeps the
        // candidate from being called fresh against content that is not its own row
        WriteFile(workspace, "repo/case.cs", "class Cased { string Term = \"ordinalneedle\"; }\n");

        var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "case.cs")]);

        result.Refreshed.ShouldBe(["demo/case.cs"]);
        result.Stale.ShouldBeEmpty();
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.ListRepos().First().Id;
        store.ListFiles(repoId).ShouldContain(f => f.Path == "case.cs",
            "the candidate is a separate row now, not a verdict borrowed from 'Case.cs'");
    }

    private static readonly TimeSpan WedgeBudget = TimeSpan.FromSeconds(30);

    [SupportedOSPlatform("linux")]
    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, uint mode);

    private static string StoredSha(string workspace, string relativePath)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        return store.FindFile(store.ListRepos().First().Id, relativePath)!.Sha256;
    }

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Squeezr.slnx not found above the test assembly");
    }

    private static int CountOccurrences(string source, string needle)
    {
        var count = 0;
        for (var at = source.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = source.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string CandidateLoopBody(string source)
    {
        const string header = "foreach (var candidate in candidates)";
        var headerAt = source.IndexOf(header, StringComparison.Ordinal);
        headerAt.ShouldBeGreaterThan(-1, $"the guard no longer has the '{header}' loop this pin reads");

        var open = source.IndexOf('{', headerAt + header.Length);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException("unbalanced braces in the candidate loop");
    }

    private static DateTime IndexedAt(string workspace, string path)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        return store.ListFiles(repoDoc.Id).First(f => f.Path == path).IndexedAt;
    }

    private static SearchQuery Query(string text) => new(text, false, null, null, null, null, null, null);

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}

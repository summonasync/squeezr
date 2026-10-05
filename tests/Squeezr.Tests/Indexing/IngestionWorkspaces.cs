using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

// A workspace whose repo holds one ordinary file and two secrets, plus the probes the ingestion tests read
// it with. Shared between the Core routes and the CLI surfaces so both arms describe the same store.
internal static class IngestionWorkspaces
{
    internal const string EnvPath = ".env";
    internal const string PemPath = "deploy/keys/monkey.pem";
    internal const string AppPath = "src/App.cs";
    internal const string EnvCanary = "T2-CANARY-ENV-VALUE";
    internal const string PemCanary = "T2-CANARY-PEM-VALUE";

    internal static string NewWorkspaceWithSecrets()
    {
        var workspace = NewEmptyWorkspace();
        WriteFile(workspace, AppPath, "public class App { public void Run() { } }\n");
        WriteFile(workspace, EnvPath, $"OPENAI_API_KEY={EnvCanary}\n");
        WriteFile(workspace, PemPath, $"{PemCanary}\n");
        return workspace;
    }

    internal static string NewWorkspaceWithPlainFile()
    {
        var workspace = NewEmptyWorkspace();
        WriteFile(workspace, AppPath, "public class App { public void Run() { } }\n");
        return workspace;
    }

    internal static string NewEmptyWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(RepoRoot(workspace));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    internal static string RepoRoot(string workspace) => Path.Combine(workspace, "repo");

    internal static string Full(string workspace, string relativePath) =>
        Path.Combine(RepoRoot(workspace), relativePath.Replace('/', Path.DirectorySeparatorChar));

    internal static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Full(workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    internal static List<string> StoredPaths(string workspace)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.ListRepos().First().Id;
        return [.. store.ListFiles(repoId).Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal)];
    }

    internal static uint RepoId(MetaStore store) => store.ListRepos().First().Id;

    internal static int LedgerWithheld(string workspace) =>
        new WorkspaceManager(workspace).GetStatus().Repos.Sum(r => r.GuardWithheld);

    internal static string ReadAscii(string path) =>
        System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path));
}

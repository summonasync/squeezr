using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Squeezr.Core.Workspace;
using RoslynWorkspace = Microsoft.CodeAnalysis.Workspace;

namespace Squeezr.Core.Indexing.Roslyn;

public sealed class RoslynSession : IDisposable
{
    private static readonly object LocatorLock = new();

    private static readonly EnumerationOptions IgnoreCaseTopDir = new() { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 };
    private static readonly EnumerationOptions IgnoreCaseAllDirs = new() { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = true, AttributesToSkip = 0 };

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly RoslynWorkspace _workspace;
    private readonly Dictionary<string, DocumentId> _documents;
    private readonly HashSet<string> _rejectedPaths = [];
    private readonly HashSet<string> _unsyncedPaths = [];
    private Solution _solution;
    private readonly string _root;

    private RoslynSession(
        RoslynWorkspace workspace,
        Solution solution,
        Dictionary<string, DocumentId> documents,
        bool semantic,
        List<string> warnings,
        string root,
        bool degraded)
    {
        _workspace = workspace;
        _solution = solution;
        _documents = documents;
        Semantic = semantic;
        Warnings = warnings;
        _root = root;
        Degraded = degraded;
    }

    public bool Semantic { get; }

    public bool Degraded { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyCollection<string> RejectedPaths => _rejectedPaths;

    public IReadOnlyCollection<string> UnsyncedPaths => _unsyncedPaths;

    public bool HasUnsynced(string relativePath) => _unsyncedPaths.Contains(relativePath);

    public bool RequiresReopen { get; private set; }

    public static RoslynSession Create(string rootPath, bool semantic)
    {
        var warnings = new List<string>();
        var root = Path.GetFullPath(rootPath);
        if (semantic)
        {
            try
            {
                return CreateMsBuildSession(root, warnings);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
            {
                warnings.Add($"MSBuild workspace unavailable ({ex.Message}); falling back to ad-hoc project");
                // a repo that does have a project to load but failed to load it today may load it tomorrow:
                // mark it so the pool keeps retrying instead of caching the fallback for the process lifetime
                if (HasProjectFile(root))
                {
                    return CreateAdHocSession(root, semantic, warnings, degraded: true);
                }
            }
        }

        return CreateAdHocSession(root, semantic, warnings, degraded: false);
    }

    public Document? FindDocument(string relativePath)
    {
        var full = FullPath(relativePath);
        return _documents.TryGetValue(full, out var id) ? _solution.GetDocument(id) : null;
    }

    public void SyncContent(IEnumerable<string> relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            var full = FullPath(relativePath);
            if (!File.Exists(full))
            {
                RemoveDocument(relativePath);
                _unsyncedPaths.Remove(relativePath);
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // keeping the document would let the parse splice this snapshot's spans with the fresh
                // disk bytes the caller reads separately; drop it and let the pass skip the file instead
                RemoveDocument(relativePath);
                _unsyncedPaths.Add(relativePath);
                continue;
            }

            _unsyncedPaths.Remove(relativePath);
            UpsertDocument(full, text, relativePath);
        }
    }

    public void RemoveDocument(string relativePath)
    {
        var full = FullPath(relativePath);
        if (_documents.Remove(full, out var id))
        {
            _solution = _solution.RemoveDocument(id);
        }
    }

    public async Task<SemanticModel?> GetSemanticModelAsync(Document document, CancellationToken cancellationToken = default) =>
        Semantic ? await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) : null;

    public void Dispose() => _workspace.Dispose();

    private string FullPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private void UpsertDocument(string fullPath, string text, string relativePath)
    {
        if (_documents.TryGetValue(fullPath, out var known))
        {
            _solution = _solution.WithDocumentText(known, SourceText.From(text));
            _rejectedPaths.Remove(relativePath);
            return;
        }

        var project = FindOwningProject(fullPath);
        if (project is null)
        {
            RequiresReopen = true;
            _rejectedPaths.Add(relativePath);
            return;
        }

        var added = DocumentInfo.Create(
            DocumentId.CreateNewId(project.Id),
            Path.GetFileName(fullPath),
            filePath: fullPath,
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(text), VersionStamp.Create())));
        _solution = _solution.AddDocument(added);
        _documents[fullPath] = added.Id;
        _rejectedPaths.Remove(relativePath);
    }

    private Project? FindOwningProject(string fullPath)
    {
        Project? best = null;
        var bestLength = -1;
        foreach (var project in _solution.Projects)
        {
            var directory = project.FilePath is null
                ? null
                : Path.GetDirectoryName(project.FilePath);
            if (directory is null || !fullPath.StartsWith(directory + Path.DirectorySeparatorChar, PathComparison))
            {
                continue;
            }

            if (directory.Length > bestLength)
            {
                best = project;
                bestLength = directory.Length;
            }
        }

        return best;
    }

    private static bool TryRegisterLocator()
    {
        lock (LocatorLock)
        {
            if (MSBuildLocator.IsRegistered)
            {
                return true;
            }

            try
            {
                MSBuildLocator.RegisterDefaults();
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                return false;
            }
        }
    }

    private static RoslynSession CreateMsBuildSession(string root, List<string> warnings)
    {
        if (!TryRegisterLocator())
        {
            throw new InvalidOperationException("Microsoft.Build.Locator could not register an MSBuild instance.");
        }

        var workspace = MSBuildWorkspace.Create();
        var solutionPath = FindSolutionFile(root);

        Solution solution;
        if (solutionPath is not null)
        {
            solution = workspace.OpenSolutionAsync(solutionPath).GetAwaiter().GetResult();
        }
        else
        {
            var project = FindProjectFile(root);
            if (project is null)
            {
                workspace.Dispose();
                throw new InvalidOperationException($"No solution or project file found under '{root}'.");
            }

            var loaded = workspace.OpenProjectAsync(project).GetAwaiter().GetResult();
            solution = loaded.Solution;
        }

        var documents = new Dictionary<string, DocumentId>(PathComparer);
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            if (document.FilePath is not null && IsUnderRoot(document.FilePath, root))
            {
                documents.TryAdd(Path.GetFullPath(document.FilePath), document.Id);
            }
        }

        if (documents.Count == 0)
        {
            workspace.Dispose();
            throw new InvalidOperationException($"Solution at '{root}' contains no source documents.");
        }

        warnings.Add($"semantic mode: {solution.Projects.Count()} project(s) via {(solutionPath is null ? "project file" : Path.GetFileName(solutionPath))}");
        return new RoslynSession(workspace, solution, documents, semantic: true, warnings, root, degraded: false);
    }

    private static string? FindSolutionFile(string root) =>
        Directory.EnumerateFiles(root, "*.sln*", IgnoreCaseTopDir)
            .Where(p => p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.EndsWith("x", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();

    private static string? FindProjectFile(string root) =>
        Directory.EnumerateFiles(root, "*.csproj", IgnoreCaseAllDirs)
            .FirstOrDefault(p => !IsBuildOutput(p));

    private static bool HasProjectFile(string root)
    {
        try
        {
            return FindSolutionFile(root) is not null || FindProjectFile(root) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static RoslynSession CreateAdHocSession(string root, bool semantic, List<string> warnings, bool degraded)
    {
        var files = Directory.EnumerateFiles(root, "*.cs", IgnoreCaseAllDirs)
            .Where(p => !IsBuildOutput(p))
            .ToList();
        if (files.Count == 0)
        {
            throw new WorkspaceException($"No C# files found under '{root}'.");
        }

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var references = DefaultMetadataReferences();
        if (semantic && references.Length == 0)
        {
            warnings.Add("no reference assemblies on disk (single-file deployment); syntax-only extraction, references unresolved");
            semantic = false;
        }

        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), "AdHoc", "AdHoc", LanguageNames.CSharp)
            .WithFilePath(Path.Combine(root, "AdHoc.csproj"))
            .WithMetadataReferences(references)
            .WithDocuments(files.Select(f =>
                DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(f),
                    filePath: f,
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(File.ReadAllText(f)), VersionStamp.Create())))));
        workspace.TryApplyChanges(workspace.CurrentSolution.AddProject(projectInfo));

        var solution = workspace.CurrentSolution;
        var documents = new Dictionary<string, DocumentId>(PathComparer);
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            if (document.FilePath is not null)
            {
                documents.TryAdd(Path.GetFullPath(document.FilePath), document.Id);
            }
        }

        warnings.Add($"ad-hoc workspace: {files.Count} file(s), project references unresolved");
        return new RoslynSession(workspace, solution, documents, semantic, warnings, root, degraded);
    }

    private static PortableExecutableReference[] DefaultMetadataReferences()
    {
        var types = new[]
        {
            typeof(object),
            typeof(Console),
            typeof(Enumerable),
            typeof(System.Runtime.GCSettings),
            typeof(System.Linq.Expressions.Expression),
            typeof(System.Collections.Generic.List<>),
            typeof(System.Threading.Tasks.Task),
            typeof(System.ComponentModel.INotifyPropertyChanged),
        };
        return types
            .Select(t => t.Assembly)
            .Distinct()
            .Where(a => !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToArray();
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderRoot(string path, string root) =>
        Path.GetFullPath(path).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);
}

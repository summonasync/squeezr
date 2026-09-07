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

    private readonly RoslynWorkspace _workspace;
    private readonly Dictionary<string, Document> _documents;
    private readonly string _root;

    private RoslynSession(RoslynWorkspace workspace, Dictionary<string, Document> documents, bool semantic, List<string> warnings, string root)
    {
        _workspace = workspace;
        _documents = documents;
        Semantic = semantic;
        Warnings = warnings;
        _root = root;
    }

    public bool Semantic { get; }

    public IReadOnlyList<string> Warnings { get; }

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
            }
        }

        return CreateAdHocSession(root, semantic, warnings);
    }

    public Document? FindDocument(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return _documents.GetValueOrDefault(full);
    }

    public async Task<SemanticModel?> GetSemanticModelAsync(Document document, CancellationToken cancellationToken = default) =>
        Semantic ? await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) : null;

    public void Dispose() => _workspace.Dispose();

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
        var solutionPath = Directory.EnumerateFiles(root, "*.sln*")
            .Where(p => p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.EndsWith("x", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();

        Solution solution;
        if (solutionPath is not null)
        {
            solution = workspace.OpenSolutionAsync(solutionPath).GetAwaiter().GetResult();
        }
        else
        {
            var project = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .FirstOrDefault(p => !IsBuildOutput(p));
            if (project is null)
            {
                workspace.Dispose();
                throw new InvalidOperationException($"No solution or project file found under '{root}'.");
            }

            var loaded = workspace.OpenProjectAsync(project).GetAwaiter().GetResult();
            solution = loaded.Solution;
        }

        var documents = solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath is not null && IsUnderRoot(d.FilePath, root))
            .ToDictionary(d => Path.GetFullPath(d.FilePath!), StringComparer.OrdinalIgnoreCase);
        if (documents.Count == 0)
        {
            workspace.Dispose();
            throw new InvalidOperationException($"Solution at '{root}' contains no source documents.");
        }

        warnings.Add($"semantic mode: {solution.Projects.Count()} project(s) via {(solutionPath is null ? "project file" : Path.GetFileName(solutionPath))}");
        return new RoslynSession(workspace, documents, semantic: true, warnings, root);
    }

    private static RoslynSession CreateAdHocSession(string root, bool semantic, List<string> warnings)
    {
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
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
            .WithMetadataReferences(references)
            .WithDocuments(files.Select(f =>
                DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(f),
                    filePath: f,
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(File.ReadAllText(f)), VersionStamp.Create())))));
        workspace.TryApplyChanges(workspace.CurrentSolution.AddProject(projectInfo));

        var documents = workspace.CurrentSolution.Projects
            .SelectMany(p => p.Documents)
            .ToDictionary(d => Path.GetFullPath(d.FilePath!), StringComparer.OrdinalIgnoreCase);
        warnings.Add($"ad-hoc workspace: {files.Count} file(s), project references unresolved");
        return new RoslynSession(workspace, documents, semantic, warnings, root);
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
        Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
}

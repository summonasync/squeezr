using System.Diagnostics;
using System.Text;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Roslyn;

namespace Squeezr.Tests.Indexing.Roslyn;

public class RoslynProviderTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Fact]
    public void CanHandle_MatchesCSharpOnly()
    {
        using var session = RoslynSession.Create(NewMiniFixture(), semantic: false);
        var provider = new RoslynProvider(session, SqueezrConfig.CreateDefault());

        provider.CanHandle("src/Program.cs", null).ShouldBeTrue();
        provider.CanHandle("src/Program.CS", null).ShouldBeTrue();
        provider.CanHandle("src/app.js", null).ShouldBeFalse();
        provider.CanHandle("src/Program.cs", "csharp").ShouldBeTrue();
        provider.CanHandle("src/Program.cs", "cs").ShouldBeTrue();
        provider.CanHandle("src/Program.cs", "javascript").ShouldBeFalse();
    }

    [Fact]
    public void SemanticParse_ExtractsSymbolsWithSignatureDocAndContainer()
    {
        var dir = NewMiniFixture();
        RestoreProject(dir);
        using var session = RoslynSession.Create(dir, semantic: true);
        session.Semantic.ShouldBeTrue();
        session.Warnings.ShouldContain(w => w.Contains("semantic mode"));

        var provider = new RoslynProvider(session, SqueezrConfig.CreateDefault());
        var result = provider.Parse(new FileRef("mini", "Calculator.cs"), File.ReadAllBytes(Path.Combine(dir, "Calculator.cs")));

        result.Language.ShouldBe("csharp");
        var add = result.Symbols.Single(s => s.Name == "Add");
        add.Kind.ShouldBe(SymbolKind.Method);
        add.Signature!.ShouldContain("Add(int a, int b)");
        add.ContainerName.ShouldBe("Calculator");
        add.DocComment.ShouldNotBeNull();
        add.DocComment!.ShouldContain("Adds two integers.");
        add.Span.StartLine.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void SemanticParse_MapsRecordToClass()
    {
        var dir = NewMiniFixture();
        RestoreProject(dir);
        using var session = RoslynSession.Create(dir, semantic: true);
        var provider = new RoslynProvider(session, SqueezrConfig.CreateDefault());

        var result = provider.Parse(new FileRef("mini", "Models.cs"), File.ReadAllBytes(Path.Combine(dir, "Models.cs")));

        var circle = result.Symbols.Single(s => s.Name == "Circle");
        circle.Kind.ShouldBe(SymbolKind.Class);
        var shape = result.Symbols.Single(s => s.Name == "IShape");
        shape.Kind.ShouldBe(SymbolKind.Interface);
        shape.DocComment.ShouldNotBeNull();
        shape.DocComment!.ShouldContain("A shape that can report its area.");
    }

    [Fact]
    public void SemanticParse_CollectsReferencesAcrossProject()
    {
        var dir = NewMiniFixture();
        RestoreProject(dir);
        using var session = RoslynSession.Create(dir, semantic: true);
        var provider = new RoslynProvider(session, SqueezrConfig.CreateDefault());

        var calculator = provider.Parse(new FileRef("mini", "Calculator.cs"), File.ReadAllBytes(Path.Combine(dir, "Calculator.cs")));
        var call = calculator.Refs.Single(r => r.Kind == RefKind.Call && r.ToName!.Contains("Add"));
        call.Line.ShouldBe(8);

        var models = provider.Parse(new FileRef("mini", "Models.cs"), File.ReadAllBytes(Path.Combine(dir, "Models.cs")));
        models.Refs.ShouldContain(r => r.Kind == RefKind.Inherit && r.ToName == "ShapeBase" && r.Line == 32);
        models.Refs.ShouldContain(r => r.Kind == RefKind.Implement && r.ToName == "IShape" && r.Line == 23);
        models.Refs.ShouldContain(r => r.Kind == RefKind.Implement && r.ToName == "IShape" && r.Line == 32);
    }

    [Fact]
    public void Chunks_ContainTokenCountsAndHashes()
    {
        var dir = NewMiniFixture();
        RestoreProject(dir);
        using var session = RoslynSession.Create(dir, semantic: true);
        var provider = new RoslynProvider(session, SqueezrConfig.CreateDefault());

        var result = provider.Parse(new FileRef("mini", "Calculator.cs"), File.ReadAllBytes(Path.Combine(dir, "Calculator.cs")));

        result.Chunks.ShouldNotBeEmpty();
        foreach (var chunk in result.Chunks)
        {
            chunk.TokenCount.ShouldBeGreaterThan(0);
            chunk.ChunkHash.Length.ShouldBe(64);
            chunk.ChunkHash.ShouldMatch("^[0-9A-F]{64}$");
            chunk.Text.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Chunks_OversizedSymbol_SplitsWithSignaturePrefix()
    {
        var dir = NewMiniFixture();
        RestoreProject(dir);
        using var session = RoslynSession.Create(dir, semantic: true);
        var config = SqueezrConfig.CreateDefault() with
        {
            Chunking = new ChunkingConfig
            {
                MaxTokensPerChunk = 24,
                FallbackWindowLines = 60,
                FallbackOverlapLines = 15
            }
        };
        var provider = new RoslynProvider(session, config);

        var result = provider.Parse(new FileRef("mini", "Models.cs"), File.ReadAllBytes(Path.Combine(dir, "Models.cs")));

        var squareChunks = result.Chunks.Where(c => c.Text.StartsWith("Square") && c.Span.StartLine >= 32).ToList();
        squareChunks.Count.ShouldBeGreaterThan(1);
        foreach (var chunk in squareChunks)
        {
            chunk.Text.ShouldStartWith("Square");
            chunk.Span.StartLine.ShouldBeGreaterThanOrEqualTo(32);
        }
    }

    [Fact]
    public void Chunks_OversizedSplit_CoversEachLineExactlyOnce()
    {
        var dir = NewMiniFixture();
        RestoreProject(dir);
        using var session = RoslynSession.Create(dir, semantic: true);
        var config = SqueezrConfig.CreateDefault() with
        {
            Chunking = new ChunkingConfig
            {
                MaxTokensPerChunk = 24,
                FallbackWindowLines = 60,
                FallbackOverlapLines = 15
            }
        };
        var provider = new RoslynProvider(session, config);

        var result = provider.Parse(new FileRef("mini", "Models.cs"), File.ReadAllBytes(Path.Combine(dir, "Models.cs")));

        var square = result.Symbols.Single(s => s.Name == "Square" && s.Kind == SymbolKind.Class);
        var lines = File.ReadAllText(Path.Combine(dir, "Models.cs")).Split('\n');
        var end = Math.Min(square.Span.EndLine, lines.Length);
        var expectedBody = string.Join("\n", lines[(square.Span.StartLine - 1)..end]);

        var prefix = square.Signature + "\n";
        var subChunks = result.Chunks
            .Where(c => c.Text.StartsWith(prefix, StringComparison.Ordinal) && c.Span.StartLine >= square.Span.StartLine)
            .OrderBy(c => c.Span.StartLine)
            .ToList();
        subChunks.Count.ShouldBeGreaterThan(1);

        var reconstructed = string.Join("\n", subChunks.Select(c => c.Text[prefix.Length..]));
        reconstructed.ShouldBe(expectedBody);
    }

    [Fact]
    public void NoSemanticMode_ParsesSyntaxOnlyWithEmptyRefs()
    {
        var dir = NewMiniFixture();
        using var session = RoslynSession.Create(dir, semantic: false);
        session.Semantic.ShouldBeFalse();
        session.Warnings.ShouldContain(w => w.Contains("ad-hoc workspace"));

        var provider = new RoslynProvider(session, SqueezrConfig.CreateDefault());
        var result = provider.Parse(new FileRef("mini", "Program.cs"), File.ReadAllBytes(Path.Combine(dir, "Program.cs")));

        result.Symbols.ShouldNotBeEmpty();
        result.Refs.ShouldBeEmpty();
        var main = result.Symbols.Single(s => s.Name == "Main");
        main.Kind.ShouldBe(SymbolKind.Method);
    }

    [Fact]
    public void BrokenSolution_FallsBackToAdHocWithWarning()
    {
        var dir = CopyFixture("RoslynBroken");
        using var session = RoslynSession.Create(dir, semantic: true);

        session.Semantic.ShouldBeTrue();
        session.Warnings.ShouldContain(w => w.Contains("MSBuild workspace unavailable"));

        var provider = new RoslynProvider(session, SqueezrConfig.CreateDefault());
        var result = provider.Parse(new FileRef("broken", "BrokenCode.cs"), File.ReadAllBytes(Path.Combine(dir, "BrokenCode.cs")));
        result.Symbols.Single(s => s.Name == "HalfBaked");
    }

    [Fact]
    public void SemanticParse_UnreferencedMember_DoesNotLeakDeclarationAsSelfReference()
    {
        var dir = NewMiniFixture();
        RestoreProject(dir);
        using var session = RoslynSession.Create(dir, semantic: true);
        var provider = new RoslynProvider(session, SqueezrConfig.CreateDefault());

        var result = provider.Parse(new FileRef("mini", "Calculator.cs"), File.ReadAllBytes(Path.Combine(dir, "Calculator.cs")));

        result.Refs.Any(r => r.ToName is not null && r.ToName.Contains("Wrap")).ShouldBeFalse();
    }

    [Fact]
    public async Task ConcurrentSessions_BothLoadSemanticWorkspaceWithoutDoubleRegistration()
    {
        var dirA = NewMiniFixture();
        var dirB = NewMiniFixture();
        RestoreProject(dirA);
        RestoreProject(dirB);

        RoslynSession? sessionA = null;
        RoslynSession? sessionB = null;
        var taskA = Task.Run(() => sessionA = RoslynSession.Create(dirA, semantic: true));
        var taskB = Task.Run(() => sessionB = RoslynSession.Create(dirB, semantic: true));
        await Task.WhenAll(taskA, taskB);

        using (sessionA!)
        using (sessionB!)
        {
            sessionA!.Semantic.ShouldBeTrue();
            sessionA.Warnings.ShouldContain(w => w.Contains("semantic mode"));
            sessionB!.Semantic.ShouldBeTrue();
            sessionB.Warnings.ShouldContain(w => w.Contains("semantic mode"));
        }
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string NewMiniFixture()
    {
        var dir = CopyFixture("RoslynMini");
        return dir;
    }

    private string CopyFixture(string name)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        var dest = TestDirs.NewTempDir();
        _tempDirs.Add(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        }

        return dest;
    }

    private static void RestoreProject(string dir)
    {
        var csproj = Directory.EnumerateFiles(dir, "*.csproj").Single();
        var startInfo = new ProcessStartInfo("dotnet", $"restore \"{csproj}\" --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(startInfo)!;
        process.WaitForExit(120_000).ShouldBeTrue();
        process.ExitCode.ShouldBe(0, process.StandardError.ReadToEnd());
    }
}


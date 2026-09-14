using Microsoft.CodeAnalysis;
using Shouldly;
using Squeezr.Core.Indexing.Roslyn;
using SqueezrRefKind = Squeezr.Core.Indexing.RefKind;

namespace Squeezr.Tests.Indexing.Roslyn;

public class ReferenceExtractorTests
{
    private const string LibSource = """
        namespace Lib;

        public class Greeter
        {
            public string Hello() => "hi";
        }
        """;

    private const string AppSource = """
        using Lib;

        namespace App;

        public class Runner
        {
            public string Run()
            {
                var greeter = new Greeter();
                return greeter.Hello();
            }
        }
        """;

    [Fact]
    public async Task FindAsync_UsageInReferencingProject_IsFoundWithCallerFilePath()
    {
        var references = RuntimeReferences();
        using var workspace = new AdhocWorkspace();
        var lib = workspace.CurrentSolution
            .AddProject("Lib", "Lib", LanguageNames.CSharp)
            .AddMetadataReferences(references);
        var libDoc = lib.AddDocument("Greeter.cs", LibSource, filePath: "/virtual/lib/Greeter.cs");
        var app = libDoc.Project.Solution
            .AddProject("App", "App", LanguageNames.CSharp)
            .AddMetadataReferences(references)
            .AddProjectReference(new ProjectReference(lib.Id));
        var appDoc = app.AddDocument("Runner.cs", AppSource, filePath: "/virtual/app/Runner.cs");

        var currentLibDoc = appDoc.Project.Solution.GetDocument(libDoc.Id)!;
        var model = await currentLibDoc.GetSemanticModelAsync(CancellationToken.None);
        var root = await currentLibDoc.GetSyntaxRootAsync(CancellationToken.None);
        var declared = SymbolExtractor.Extract(root!, model!, LibSource);

        var refs = await ReferenceExtractor.FindAsync(declared, currentLibDoc, CancellationToken.None);

        var greeterUse = refs.Single(r => r.ToName == "Greeter");
        greeterUse.FromPath.ShouldBe("/virtual/app/Runner.cs");
        greeterUse.Kind.ShouldBe(SqueezrRefKind.Reference);
        greeterUse.Line.ShouldBe(9);
        greeterUse.EnclosingName.ShouldBe("Runner.Run");

        var helloCall = refs.Single(r => r.ToName!.Contains("Hello"));
        helloCall.FromPath.ShouldBe("/virtual/app/Runner.cs");
        helloCall.Kind.ShouldBe(SqueezrRefKind.Call);
        helloCall.Line.ShouldBe(10);
    }

    internal static IReadOnlyList<MetadataReference> RuntimeReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return Directory.EnumerateFiles(runtimeDir, "*.dll")
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    }
}

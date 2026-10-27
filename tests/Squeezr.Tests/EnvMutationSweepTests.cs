using System.Text.RegularExpressions;
using Shouldly;

namespace Squeezr.Tests;

public class EnvMutationSweepTests
{
    private static readonly Regex SerializedDefinition =
        new(@"\[CollectionDefinition\(""(?<name>[^""]+)""(?<body>[^\)]*)\)\]", RegexOptions.Compiled);

    [Fact]
    public void TestTree_ClassesTouchingProcessEnvOrProcesses_AllSitInSerializedCollections()
    {
        var testsRoot = TestsRoot();
        var offenders = EnvMutationSweep.Scan(testsRoot, SerializedCollections(testsRoot));

        offenders.ShouldBeEmpty(
            $"env/process-touching test classes must sit in a DisableParallelization collection: {Describe(offenders)}");
    }

    [Fact]
    public void Scan_ClassMutatingEnvWithoutCollection_IsReportedWithItsName()
    {
        using var scratch = new ScratchDir();
        scratch.Write("OffenderTests.cs",
            """
            public class OffenderTests
            {
                [Fact]
                public void Do_Thing()
                {
                    Environment.SetEnvironmentVariable("SQUEEZR_HOME", null);
                }
            }
            """);

        var offenders = EnvMutationSweep.Scan(scratch.Root, ["ProcessGlobals"]);

        var offender = offenders.Single(o => o.ClassName == "OffenderTests");
        offender.Evidence.ShouldContain("Environment.SetEnvironmentVariable");
        offender.RelativePath.ShouldBe("OffenderTests.cs");
    }

    [Fact]
    public void Scan_SameClassUnderSerializedCollection_IsClean()
    {
        using var scratch = new ScratchDir();
        scratch.Write("CoveredTests.cs",
            """
            [Collection("ProcessGlobals")]
            public class CoveredTests
            {
                [Fact]
                public void Do_Thing()
                {
                    Environment.SetEnvironmentVariable("SQUEEZR_HOME", null);
                }
            }
            """);

        EnvMutationSweep.Scan(scratch.Root, ["ProcessGlobals"]).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_MutationInNestedHelper_OfUncoveredTestClass_NamesTheOuterClass()
    {
        using var scratch = new ScratchDir();
        scratch.Write("OuterTests.cs",
            """
            public class OuterTests
            {
                [Fact]
                public void Do_Thing() => Nested.Mutate();

                private sealed class Nested
                {
                    public static void Mutate() => Environment.SetEnvironmentVariable("HOME", null);
                }
            }
            """);

        offenders(scratch).Select(o => o.ClassName).ShouldBe(["OuterTests"]);
    }

    [Fact]
    public void Scan_MutationInNestedHelper_OfCoveredTestClass_IsClean()
    {
        using var scratch = new ScratchDir();
        scratch.Write("OuterTests.cs",
            """
            [Collection("ProcessGlobals")]
            public class OuterTests
            {
                [Fact]
                public void Do_Thing() => Nested.Mutate();

                private sealed class Nested
                {
                    public static void Mutate() => Environment.SetEnvironmentVariable("HOME", null);
                }
            }
            """);

        offenders(scratch).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_ProcessSpawningHelperWithoutTests_IsIgnored()
    {
        using var scratch = new ScratchDir();
        scratch.Write("CliTestProcess.cs",
            """
            public static class CliTestProcess
            {
                public static Process Start(string home) =>
                    Process.Start(new ProcessStartInfo("squeezr") { Environment["SQUEEZR_HOME"] = home });
            }
            """);

        offenders(scratch).ShouldBeEmpty();
    }

    private static IReadOnlyList<EnvMutationSweep.Offender> offenders(ScratchDir scratch) =>
        EnvMutationSweep.Scan(scratch.Root, ["ProcessGlobals"]);

    private static string Describe(IReadOnlyList<EnvMutationSweep.Offender> offenders) =>
        string.Join("; ", offenders.Select(o => $"{o.ClassName} ({o.RelativePath}: {string.Join(", ", o.Evidence)})"));

    private static string TestsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            dir = dir.Parent;
        dir.ShouldNotBeNull();
        return Path.Combine(dir!.FullName, "tests");
    }

    private static IReadOnlyList<string> SerializedCollections(string testsRoot)
    {
        var text = File.ReadAllText(Path.Combine(testsRoot, "Squeezr.Tests", "CollectionDefinitions.cs"));
        return SerializedDefinition.Matches(text)
            .Where(match => match.Groups["body"].Value.Contains("DisableParallelization = true"))
            .Select(match => match.Groups["name"].Value)
            .ToList();
    }

    private sealed class ScratchDir : IDisposable
    {
        public ScratchDir()
        {
            Root = Directory.CreateTempSubdirectory("env-sweep-").FullName;
        }

        public string Root { get; }

        public void Write(string fileName, string content) =>
            File.WriteAllText(Path.Combine(Root, fileName), content);

        public void Dispose() => Directory.Delete(Root, true);
    }
}

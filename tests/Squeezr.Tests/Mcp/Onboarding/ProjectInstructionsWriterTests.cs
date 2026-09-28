using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

public class ProjectInstructionsWriterTests
{
    [Fact]
    public void Refresh_EmptyProject_CreatesAgentsMdWithSingleBlock()
    {
        var root = TestDirs.NewTempDir();

        var change = ProjectInstructionsWriter.Refresh(root);

        var agents = Path.Combine(root, "AGENTS.md");
        change.Written.ShouldBe([agents]);
        change.Orphaned.ShouldBeEmpty();
        var content = File.ReadAllText(agents);
        CountBlocks(content).ShouldBe(1);
        content.ShouldStartWith(ProjectInstructions.StartMarker);
        content.ShouldEndWith(ProjectInstructions.EndMarker + "\n");
    }

    [Fact]
    public void Refresh_RealClaudeFile_PlacesBlockAfterFirstHeadingAndCreatesAgents()
    {
        var root = TestDirs.NewTempDir();
        var claude = Path.Combine(root, "CLAUDE.md");
        const string foreign = "# Project Guide\n\nFollow the conventions.\n";
        File.WriteAllText(claude, foreign);

        var change = ProjectInstructionsWriter.Refresh(root);

        var agents = Path.Combine(root, "AGENTS.md");
        change.Written.ShouldBe([agents, claude]);
        change.Orphaned.ShouldBeEmpty();
        File.ReadAllText(claude).ShouldBe("# Project Guide\n\n" + ProjectInstructions.Block + "\n\nFollow the conventions.\n");
        CountBlocks(File.ReadAllText(agents)).ShouldBe(1);
    }

    [Fact]
    public void Refresh_FileWithoutHeading_AppendsBlockAtEnd()
    {
        var root = TestDirs.NewTempDir();
        var qwen = Path.Combine(root, "QWEN.md");
        const string foreign = "Just prose, no heading.\n";
        File.WriteAllText(qwen, foreign);

        ProjectInstructionsWriter.Refresh(root);

        var content = File.ReadAllText(qwen);
        CountBlocks(content).ShouldBe(1);
        content.ShouldContain(foreign + "\n" + ProjectInstructions.StartMarker);
    }

    [Fact]
    public void Refresh_PointerClaude_LeavesPointerBytesUntouched()
    {
        var root = TestDirs.NewTempDir();
        var claude = Path.Combine(root, "CLAUDE.md");
        const string pointer = "<!-- managed elsewhere -->\n@AGENTS.md\n";
        File.WriteAllText(claude, pointer);

        var change = ProjectInstructionsWriter.Refresh(root);

        change.Written.ShouldBe([Path.Combine(root, "AGENTS.md")]);
        change.Orphaned.ShouldBeEmpty();
        File.ReadAllText(claude).ShouldBe(pointer);
        CountBlocks(File.ReadAllText(claude)).ShouldBe(0);
    }

    [Fact]
    public void Refresh_PointerQwen_LeavesPointerBytesUntouched()
    {
        var root = TestDirs.NewTempDir();
        var qwen = Path.Combine(root, "QWEN.md");
        const string pointer = "@AGENTS.md\n";
        File.WriteAllText(qwen, pointer);

        var change = ProjectInstructionsWriter.Refresh(root);

        change.Written.ShouldBe([Path.Combine(root, "AGENTS.md")]);
        change.Orphaned.ShouldBeEmpty();
        File.ReadAllText(qwen).ShouldBe(pointer);
        CountBlocks(File.ReadAllText(qwen)).ShouldBe(0);
    }

    [Fact]
    public void Refresh_Twice_KeepsExactlyOneBlockAndStaysIdentical()
    {
        var root = TestDirs.NewTempDir();
        var claude = Path.Combine(root, "CLAUDE.md");
        File.WriteAllText(claude, "# Existing\n");

        ProjectInstructionsWriter.Refresh(root);
        ProjectInstructionsWriter.Refresh(root);
        var once = File.ReadAllText(claude);
        ProjectInstructionsWriter.Refresh(root);
        var twice = File.ReadAllText(claude);

        CountBlocks(once).ShouldBe(1);
        twice.ShouldBe(once);
    }

    [Fact]
    public void Upsert_OutdatedProjectBlock_ReplacesOnlyBlockContent()
    {
        var root = TestDirs.NewTempDir();
        var claude = Path.Combine(root, "CLAUDE.md");
        const string foreign = "# Rules\n";
        File.WriteAllText(claude, foreign + "\n" + ProjectInstructions.StartMarker + "\nold text\n" + ProjectInstructions.EndMarker + "\n");

        ProjectInstructionsWriter.Upsert(claude);

        var content = File.ReadAllText(claude);
        CountBlocks(content).ShouldBe(1);
        content.ShouldNotContain("old text");
        content.ShouldContain(foreign);
        content.ShouldContain(ProjectInstructions.Block);
    }

    [Fact]
    public void Upsert_BlockDeepInFile_ReplacesInPlaceWithoutMovingOrDuplicating()
    {
        var root = TestDirs.NewTempDir();
        var qwen = Path.Combine(root, "QWEN.md");
        const string before = "# Rules\n\n## Identity\n\nBe terse.\n\n";
        const string after = "\n\n## Tools\n\nPrefer the index.\n";
        File.WriteAllText(qwen,
            before + ProjectInstructions.StartMarker + "\nold text\n" + ProjectInstructions.EndMarker + after);

        ProjectInstructionsWriter.Upsert(qwen);
        ProjectInstructionsWriter.Upsert(qwen);

        var content = File.ReadAllText(qwen);
        CountBlocks(content).ShouldBe(1);
        content.ShouldNotContain("old text");
        content.ShouldBe(before + ProjectInstructions.Block + after);
    }

    [Fact]
    public void Remove_FileWithForeignContent_RestoresOriginalBytes()
    {
        var root = TestDirs.NewTempDir();
        var claude = Path.Combine(root, "CLAUDE.md");
        const string foreign = "# Guide\n\nKeep this byte-for-byte.\n";
        File.WriteAllText(claude, foreign);
        ProjectInstructionsWriter.Upsert(claude);

        ProjectInstructionsWriter.Remove(claude);

        File.ReadAllText(claude).ShouldBe(foreign);
    }

    [Fact]
    public void Remove_OnlyBlock_LeavesEmptyFileBehind()
    {
        var root = TestDirs.NewTempDir();
        var agents = Path.Combine(root, "AGENTS.md");
        ProjectInstructionsWriter.Refresh(root);
        File.Exists(agents).ShouldBeTrue();

        ProjectInstructionsWriter.Remove(agents);

        File.Exists(agents).ShouldBeTrue();
        File.ReadAllText(agents).ShouldBeEmpty();
    }

    [Fact]
    public void Strip_RemovesBlockFromEveryCarrierAndKeepsFiles()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, "QWEN.md"), "# Rules\n\nBe terse.\n");
        ProjectInstructionsWriter.Refresh(root);

        var stripped = ProjectInstructionsWriter.Strip(root);

        var agents = Path.Combine(root, "AGENTS.md");
        var qwen = Path.Combine(root, "QWEN.md");
        stripped.ShouldBe([agents, qwen]);
        File.ReadAllText(agents).ShouldBeEmpty();
        File.ReadAllText(qwen).ShouldBe("# Rules\n\nBe terse.\n");
    }

    [Fact]
    public void Strip_NoCarriers_NothingStripped()
    {
        var root = TestDirs.NewTempDir();

        ProjectInstructionsWriter.Strip(root).ShouldBeEmpty();
    }

    [Fact]
    public void Refresh_OrphanStartMarker_SkipsFileAndReportsOrphan()
    {
        var root = TestDirs.NewTempDir();
        var agents = Path.Combine(root, "AGENTS.md");
        const string orphan = "# Guide\n\n" + ProjectInstructions.StartMarker + "\nquoted start only\n";
        File.WriteAllText(agents, orphan);

        var change = ProjectInstructionsWriter.Refresh(root);

        change.Written.ShouldBeEmpty();
        change.Orphaned.ShouldBe([agents]);
        File.ReadAllText(agents).ShouldBe(orphan);
    }

    [Fact]
    public void Refresh_OrphanEndMarker_SkipsFileAndReportsOrphan()
    {
        var root = TestDirs.NewTempDir();
        var qwen = Path.Combine(root, "QWEN.md");
        const string orphan = "# Rules\n\ntrailing orphan\n" + ProjectInstructions.EndMarker + "\n";
        File.WriteAllText(qwen, orphan);

        var change = ProjectInstructionsWriter.Refresh(root);

        change.Written.ShouldBe([Path.Combine(root, "AGENTS.md")]);
        change.Orphaned.ShouldBe([qwen]);
        File.ReadAllText(qwen).ShouldBe(orphan);
    }

    [Fact]
    public void Upsert_OrphanMarker_LeavesFileUntouched()
    {
        var root = TestDirs.NewTempDir();
        var claude = Path.Combine(root, "CLAUDE.md");
        const string orphan = ProjectInstructions.StartMarker + "\nnever closed\n";
        File.WriteAllText(claude, orphan);

        ProjectInstructionsWriter.Upsert(claude);

        File.ReadAllText(claude).ShouldBe(orphan);
    }

    [Fact]
    public void Upsert_FileWithOnlyGlobalBlock_AppendsProjectBlockOutsideIt()
    {
        var root = TestDirs.NewTempDir();
        var agents = Path.Combine(root, "AGENTS.md");
        InstructionsWriter.Upsert(agents);

        ProjectInstructionsWriter.Upsert(agents);

        var content = File.ReadAllText(agents);
        content.ShouldContain(SqueezrInstructions.Block);
        content.ShouldContain(ProjectInstructions.Block);
        content.IndexOf(ProjectInstructions.StartMarker, StringComparison.Ordinal)
            .ShouldBeGreaterThan(content.IndexOf(SqueezrInstructions.EndMarker, StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalUpsert_FileWithOnlyProjectBlock_AppendsGlobalBlockOutsideIt()
    {
        var root = TestDirs.NewTempDir();
        var agents = Path.Combine(root, "AGENTS.md");
        ProjectInstructionsWriter.Upsert(agents);

        InstructionsWriter.Upsert(agents);

        var content = File.ReadAllText(agents);
        content.ShouldContain(SqueezrInstructions.Block);
        content.ShouldContain(ProjectInstructions.Block);
        content.IndexOf(SqueezrInstructions.StartMarker, StringComparison.Ordinal)
            .ShouldBeGreaterThan(content.IndexOf(ProjectInstructions.EndMarker, StringComparison.Ordinal));
    }

    private static int CountBlocks(string content) =>
        content.Split(ProjectInstructions.StartMarker).Length - 1;
}

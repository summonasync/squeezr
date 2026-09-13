using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;
using System.Text;

namespace Squeezr.Tests.Mcp.Onboarding;

public class InstructionsWriterTests
{
    [Fact]
    public void Upsert_MissingFile_CreatesFileWithSingleBlock()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "AGENTS.md");

        InstructionsWriter.Upsert(path);

        var content = File.ReadAllText(path);
        CountBlocks(content).ShouldBe(1);
        content.ShouldStartWith(SqueezrInstructions.StartMarker);
        content.ShouldEndWith(SqueezrInstructions.EndMarker + "\n");
    }

    [Fact]
    public void Upsert_FileWithForeignContent_AppendsBlockAndPreservesContent()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "CLAUDE.md");
        const string foreign = "# Project Guide\n\nFollow the conventions.\n";
        File.WriteAllText(path, foreign);

        InstructionsWriter.Upsert(path);

        var content = File.ReadAllText(path);
        CountBlocks(content).ShouldBe(1);
        content.ShouldStartWith(foreign);
        content.ShouldContain(foreign + "\n" + SqueezrInstructions.StartMarker);
    }

    [Fact]
    public void Upsert_Repeated_KeepsExactlyOneBlockAndStaysIdentical()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "AGENTS.md");
        File.WriteAllText(path, "# Existing\n");

        InstructionsWriter.Upsert(path);
        InstructionsWriter.Upsert(path);
        var once = File.ReadAllText(path);
        InstructionsWriter.Upsert(path);
        var twice = File.ReadAllText(path);

        CountBlocks(once).ShouldBe(1);
        twice.ShouldBe(once);
    }

    [Fact]
    public void Upsert_OutdatedBlock_ReplacesOnlyBlockContent()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "QWEN.md");
        const string foreign = "# Rules\n";
        File.WriteAllText(path, foreign + "\n" + SqueezrInstructions.StartMarker + "\nold text\n" + SqueezrInstructions.EndMarker + "\n");

        InstructionsWriter.Upsert(path);

        var content = File.ReadAllText(path);
        CountBlocks(content).ShouldBe(1);
        content.ShouldNotContain("old text");
        content.ShouldContain(foreign);
        content.ShouldContain(SqueezrInstructions.Block);
    }

    [Fact]
    public void Remove_FileWithForeignContent_RestoresOriginalBytes()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "AGENTS.md");
        const string foreign = "# Guide\n\nKeep this byte-for-byte.\n";
        File.WriteAllText(path, foreign);
        InstructionsWriter.Upsert(path);

        InstructionsWriter.Remove(path);

        File.ReadAllText(path).ShouldBe(foreign);
    }

    [Fact]
    public void Remove_BlockBetweenForeignContent_PreservesSurroundingBytes()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "AGENTS.md");
        const string foreign = "line A\n\nline B";
        File.WriteAllText(path, "line A\n\n" + SqueezrInstructions.Block + "\n\nline B");

        InstructionsWriter.Remove(path);

        File.ReadAllText(path).ShouldBe(foreign);
    }

    [Fact]
    public void Remove_OnlyBlock_DeletesFile()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "AGENTS.md");
        InstructionsWriter.Upsert(path);
        File.Exists(path).ShouldBeTrue();

        InstructionsWriter.Remove(path);

        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void Remove_BlockAbsent_LeavesFileUntouched()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "AGENTS.md");
        const string foreign = "# No block here\n";
        File.WriteAllText(path, foreign);

        InstructionsWriter.Remove(path);

        File.ReadAllText(path).ShouldBe(foreign);
    }

    [Fact]
    public void Roundtrip_BomFile_PreservesBomAndOriginalBytes()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "AGENTS.md");
        const string foreign = "# Guide\r\n\r\nKeep this byte-for-byte.\r\n";
        File.WriteAllText(path, foreign, new UTF8Encoding(true));
        var original = File.ReadAllBytes(path);

        InstructionsWriter.Upsert(path);
        InstructionsWriter.Remove(path);

        File.ReadAllBytes(path).ShouldBe(original);
    }

    [Fact]
    public void Upsert_BomFile_PreservesBom()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "AGENTS.md");
        File.WriteAllText(path, "# Guide\n", new UTF8Encoding(true));

        InstructionsWriter.Upsert(path);

        var bytes = File.ReadAllBytes(path);
        bytes[0].ShouldBe((byte)0xEF);
        bytes[1].ShouldBe((byte)0xBB);
        bytes[2].ShouldBe((byte)0xBF);
        Encoding.UTF8.GetString(bytes[3..]).ShouldStartWith("# Guide\n");
    }

    [Fact]
    public void Remove_StartMarkerWithoutEndMarker_LeavesFileUntouched()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "AGENTS.md");
        var original = Encoding.UTF8.GetBytes("# Docs\n\n" + SqueezrInstructions.StartMarker + "\nquoted but never closed\ntrailing content\n");
        File.WriteAllBytes(path, original);

        InstructionsWriter.Remove(path);

        File.ReadAllBytes(path).ShouldBe(original);
    }

    private static int CountBlocks(string content) =>
        content.Split(SqueezrInstructions.StartMarker).Length - 1;
}

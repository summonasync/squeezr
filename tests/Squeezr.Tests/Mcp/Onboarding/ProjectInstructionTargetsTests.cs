using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

public class ProjectInstructionTargetsTests
{
    [Fact]
    public void Resolve_EmptyProject_TargetsAgentsMdOnly()
    {
        var root = TestDirs.NewTempDir();

        var targets = ProjectInstructionTargets.Resolve(root);

        targets.ShouldBe([Path.Combine(root, "AGENTS.md")]);
    }

    [Fact]
    public void Resolve_RealClaudeAndQwenFiles_TargetsBoth()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "# Guide\n\nReal content.\n");
        File.WriteAllText(Path.Combine(root, "QWEN.md"), "# Rules\n\nBe terse.\n");

        var targets = ProjectInstructionTargets.Resolve(root);

        targets.ShouldBe([
            Path.Combine(root, "AGENTS.md"),
            Path.Combine(root, "CLAUDE.md"),
            Path.Combine(root, "QWEN.md"),
        ]);
    }

    [Fact]
    public void Resolve_OneLineImportPointer_SkipsFile()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "@AGENTS.md\n");
        File.WriteAllText(Path.Combine(root, "QWEN.md"), "  @AGENTS.md  \n\n");

        var targets = ProjectInstructionTargets.Resolve(root);

        targets.ShouldBe([Path.Combine(root, "AGENTS.md")]);
    }

    [Fact]
    public void Resolve_PointerWrappedInCommentsAndWhitespace_SkipsFile()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "<!-- points to the real file -->\n\n@AGENTS.md\n\n<!-- end -->\n");

        var targets = ProjectInstructionTargets.Resolve(root);

        targets.ShouldBe([Path.Combine(root, "AGENTS.md")]);
    }

    [Fact]
    public void Resolve_MultiLineCommentWrappedPointer_SkipsFile()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, "QWEN.md"), "<!--\n  all content lives in AGENTS.md\n-->\n@AGENTS.md\n");

        var targets = ProjectInstructionTargets.Resolve(root);

        targets.ShouldBe([Path.Combine(root, "AGENTS.md")]);
    }

    [Fact]
    public void Resolve_ImportLinePlusRealContent_TargetsFile()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "@AGENTS.md\n\nPlus our own rules on top.\n");

        var targets = ProjectInstructionTargets.Resolve(root);

        targets.ShouldBe([Path.Combine(root, "AGENTS.md"), Path.Combine(root, "CLAUDE.md")]);
    }

    [Fact]
    public void Resolve_TwoImportLines_TargetsFile()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, "QWEN.md"), "@AGENTS.md\n@docs/more.md\n");

        var targets = ProjectInstructionTargets.Resolve(root);

        targets.ShouldBe([Path.Combine(root, "AGENTS.md"), Path.Combine(root, "QWEN.md")]);
    }

    [Fact]
    public void Resolve_BareAtLine_TargetsFile()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "@\n");

        var targets = ProjectInstructionTargets.Resolve(root);

        targets.ShouldBe([Path.Combine(root, "AGENTS.md"), Path.Combine(root, "CLAUDE.md")]);
    }

    [Fact]
    public void Resolve_NonImportSingleLine_TargetsFile()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "See AGENTS.md for the real rules.\n");

        var targets = ProjectInstructionTargets.Resolve(root);

        targets.ShouldBe([Path.Combine(root, "AGENTS.md"), Path.Combine(root, "CLAUDE.md")]);
    }

    [Fact]
    public void Resolve_UnclosedCommentWithImportLine_TargetsFile()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "<!-- dangling\n@AGENTS.md\n");

        var targets = ProjectInstructionTargets.Resolve(root);

        targets.ShouldBe([Path.Combine(root, "AGENTS.md"), Path.Combine(root, "CLAUDE.md")]);
    }
}

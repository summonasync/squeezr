using Shouldly;
using Squeezr.Cli.Mcp.Onboarding;
using System.Text.Json.Nodes;

namespace Squeezr.Tests.Mcp.Onboarding;

public class TomlMcpConfigWriterTests
{
    private const string CommandPath = "/opt/squeezr/bin/squeezr";
    private const string ServerName = "squeezr";

    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Mcp", "Onboarding", "Fixtures", "toml");

    [Fact]
    public async Task Install_MissingFile_CreatesSection()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "nested", "config.toml");

        await InstallAsync(path);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("new-file.expected.toml"));
    }

    [Fact]
    public async Task Install_EmptyFile_CreatesSection()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "config.toml");
        await File.WriteAllTextAsync(path, string.Empty);

        await InstallAsync(path);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("new-file.expected.toml"));
    }

    [Fact]
    public async Task Install_OtherTables_AppendsNewSection()
    {
        var path = CopyToTemp("other-tables.input.toml");

        await InstallAsync(path);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("other-tables.expected.toml"));
    }

    [Fact]
    public async Task Install_ExistingSection_ReplacesInPlace()
    {
        var path = CopyToTemp("replace-section.input.toml");

        await InstallAsync(path);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("replace-section.expected.toml"));
    }

    [Fact]
    public async Task Install_Twice_ProducesIdenticalFile()
    {
        var path = CopyToTemp("other-tables.input.toml");
        var entry = BuildEntry();

        await TomlMcpConfigWriter.InstallAsync(path, ServerName, entry, CancellationToken.None);
        var first = File.ReadAllText(path);
        await TomlMcpConfigWriter.InstallAsync(path, ServerName, entry, CancellationToken.None);
        var second = File.ReadAllText(path);

        second.ShouldBe(first);
        Normalize(second).ShouldBe(Expected("other-tables.expected.toml"));
    }

    [Fact]
    public async Task Remove_ExistingSection_KeepsNeighbors()
    {
        var path = CopyToTemp("remove-section.input.toml");

        await TomlMcpConfigWriter.RemoveAsync(path, ServerName, CancellationToken.None);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("remove-section.expected.toml"));
    }

    [Fact]
    public async Task Remove_AbsentSection_LeavesFileUntouched()
    {
        var path = CopyToTemp("other-tables.input.toml");
        var original = File.ReadAllText(path);

        await TomlMcpConfigWriter.RemoveAsync(path, ServerName, CancellationToken.None);

        File.ReadAllText(path).ShouldBe(original);
    }

    [Fact]
    public async Task Remove_MissingFile_DoesNotCreateFile()
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "config.toml");

        await TomlMcpConfigWriter.RemoveAsync(path, ServerName, CancellationToken.None);

        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task Install_DottedTopLevelKeys_Throws()
    {
        var path = CopyToTemp("dotted-keys.input.toml");
        var original = File.ReadAllText(path);

        var exception = await Should.ThrowAsync<InvalidDataException>(() => InstallAsync(path));

        exception.Message.ShouldContain("mcp_servers.squeezr");
        File.ReadAllText(path).ShouldBe(original);
    }

    [Fact]
    public async Task Install_DottedKeysInParentTable_Throws()
    {
        var path = CopyToTemp("dotted-keys-in-parent.input.toml");

        await Should.ThrowAsync<InvalidDataException>(() => InstallAsync(path));
    }

    [Fact]
    public async Task Install_ScalarMcpServersBinding_Throws()
    {
        var path = CopyToTemp("scalar-mcp-servers.input.toml");
        var original = File.ReadAllText(path);

        var exception = await Should.ThrowAsync<InvalidDataException>(() => InstallAsync(path));

        exception.Message.ShouldContain("mcp_servers");
        exception.Message.ShouldContain(path);
        File.ReadAllText(path).ShouldBe(original);
    }

    [Fact]
    public async Task Remove_WithSubTables_RemovesWholeEntry()
    {
        var path = CopyToTemp("remove-with-subtable.input.toml");

        await TomlMcpConfigWriter.RemoveAsync(path, ServerName, CancellationToken.None);

        Normalize(File.ReadAllText(path)).ShouldBe(Expected("remove-with-subtable.expected.toml"));
    }

    [Fact]
    public async Task Install_OverExistingFile_CreatesBackupWithOriginalContent()
    {
        var path = CopyToTemp("other-tables.input.toml");
        var original = File.ReadAllText(path);

        await InstallAsync(path);

        File.Exists(path + ".bak").ShouldBeTrue();
        File.ReadAllText(path + ".bak").ShouldBe(original);
    }

    private static async Task InstallAsync(string path)
    {
        var entry = BuildEntry();
        await TomlMcpConfigWriter.InstallAsync(path, ServerName, entry, CancellationToken.None);
    }

    private static JsonObject BuildEntry() =>
        ServerEntryBuilder.Build(McpClientRegistry.Get(McpClientId.Codex), CommandPath, watch: true);

    private static string CopyToTemp(string fixtureName)
    {
        var path = Path.Combine(TestDirs.NewTempDir(), "config.toml");
        File.Copy(Path.Combine(FixturesDir, fixtureName), path);
        return path;
    }

    private static string Expected(string fixtureName) =>
        Normalize(File.ReadAllText(Path.Combine(FixturesDir, fixtureName)))
            .Replace(CommandPath, Path.GetFullPath(CommandPath).Replace("\\", "\\\\"));

    private static string Normalize(string content) => content.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
}

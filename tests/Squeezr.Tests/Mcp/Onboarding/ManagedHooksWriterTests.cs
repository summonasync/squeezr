using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Tests.Mcp.Onboarding;

public sealed class ManagedHooksWriterTests
{
    private static readonly JsonSerializerOptions CanonicalOptions = new() { WriteIndented = true };

    [Fact]
    public void ForClient_KimiAndCodex_ReturnsNullForUnverifiedLayouts()
    {
        HooksBlock.ForClient(McpClientRegistry.Get(McpClientId.Kimi), "/opt/squeezr/squeezr").ShouldBeNull();
        HooksBlock.ForClient(McpClientRegistry.Get(McpClientId.Codex), "/opt/squeezr/squeezr").ShouldBeNull();
    }

    [Fact]
    public void ForClient_QwenAnchorsTheSharedHomePayloadId()
    {
        var payloadId = HookPreReadToolNames.PayloadIdFor("qwen");

        payloadId.ShouldNotBeNull();
        HookPreReadToolNames.IsReadTool(payloadId).ShouldBeTrue();
        HooksBlock.ForClient(McpClientRegistry.Get(McpClientId.Qwen), "/opt/squeezr/squeezr")!
            .Matcher.ShouldBe($"^{payloadId}$");
    }

    [Fact]
    public void ForClient_ClaudeArmsTheSharedHomePayloadIdExactly()
    {
        var payloadId = HookPreReadToolNames.PayloadIdFor("claude");

        payloadId.ShouldNotBeNull();
        HookPreReadToolNames.IsReadTool(payloadId).ShouldBeTrue();
        HooksBlock.ForClient(McpClientRegistry.Get(McpClientId.Claude), "/opt/squeezr/squeezr")!
            .Matcher.ShouldBe(payloadId);
    }

    [Fact]
    public void ForClient_QuotedCommandPathWhenItContainsSpace()
    {
        var block = HooksBlock.ForClient(McpClientRegistry.Get(McpClientId.Qwen), "/opt/my tools/squeezr");

        block!.Command.ShouldBe("\"/opt/my tools/squeezr\" hook pre-read");
    }

    [Fact]
    public async Task Upsert_MissingFile_CreatesDocumentWithTheManagedEntry()
    {
        var path = ScratchPath();
        var block = BlockFor(McpClientId.Qwen);

        var wrote = await UpsertAsync(path, block);

        wrote.ShouldBeTrue();
        File.Exists(path).ShouldBeTrue();
        ManagedEntryCount(path, block).ShouldBe(1);
    }

    [Fact]
    public async Task Upsert_ForeignDocument_KeepsMcpServersSubtreeBytesAndForeignEntries()
    {
        var path = ScratchPath();
        var block = BlockFor(McpClientId.Qwen);
        var foreignEntry = ForeignEntry("Bash", "echo keep-me");
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["other"] = new JsonObject { ["command"] = "other-cmd" } },
            ["hooks"] = new JsonObject { ["PreToolUse"] = new JsonArray(foreignEntry.DeepClone()) },
            ["theme"] = "dark",
        };
        WriteCanonical(path, root);
        var serversBefore = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!.ToJsonString(CanonicalOptions);
        var foreignBefore = foreignEntry.ToJsonString(CanonicalOptions);

        await UpsertAsync(path, block);

        var after = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        after["mcpServers"]!.ToJsonString(CanonicalOptions).ShouldBe(serversBefore);
        after["hooks"]!["PreToolUse"]!.AsArray()
            .Count(entry => entry!.ToJsonString(CanonicalOptions) == foreignBefore).ShouldBe(1);
        after["theme"]!.GetValue<string>().ShouldBe("dark");
        ManagedEntryCount(path, block).ShouldBe(1);
    }

    [Fact]
    public async Task Upsert_Twice_ExactlyOneManagedEntryAndNoSecondWrite()
    {
        var path = ScratchPath();
        var block = BlockFor(McpClientId.Qwen);

        (await UpsertAsync(path, block)).ShouldBeTrue();
        var bytesAfterFirst = File.ReadAllText(path);
        (await UpsertAsync(path, block)).ShouldBeFalse();

        File.ReadAllText(path).ShouldBe(bytesAfterFirst);
        ManagedEntryCount(path, block).ShouldBe(1);
    }

    [Fact]
    public async Task Remove_StripsManagedEntry_WholeDocumentBytesBackToCanonicalPreImage()
    {
        var path = ScratchPath();
        var block = BlockFor(McpClientId.Qwen);
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["other"] = new JsonObject { ["command"] = "other-cmd" } },
            ["theme"] = "dark",
        };
        WriteCanonical(path, root);
        var preImage = File.ReadAllText(path);
        await UpsertAsync(path, block);

        var removed = await ManagedHooksWriter.RemoveAsync(path, block, CancellationToken.None);

        removed.ShouldBeTrue();
        File.ReadAllText(path).ShouldBe(preImage);
    }

    [Fact]
    public async Task Remove_ForeignEntrySurvivesInstallAndRemoveUnchanged()
    {
        var path = ScratchPath();
        var block = BlockFor(McpClientId.Claude);
        var foreignEntry = ForeignEntry("Edit", "echo foreign");
        var root = new JsonObject
        {
            ["hooks"] = new JsonObject { ["PreToolUse"] = new JsonArray(foreignEntry.DeepClone()) },
        };
        WriteCanonical(path, root);
        var foreignBefore = foreignEntry.ToJsonString(CanonicalOptions);
        await UpsertAsync(path, block);

        await ManagedHooksWriter.RemoveAsync(path, block, CancellationToken.None);

        var entries = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!["PreToolUse"]!.AsArray();
        entries.Count.ShouldBe(1);
        entries[0]!.ToJsonString(CanonicalOptions).ShouldBe(foreignBefore);
        ManagedEntryCount(path, block).ShouldBe(0);
    }

    [Fact]
    public async Task Remove_NeverArmedFile_IsNoOpSuccessWithoutTouchingBytesOrBackup()
    {
        var path = ScratchPath();
        var block = BlockFor(McpClientId.Qwen);
        WriteCanonical(path, new JsonObject
        {
            ["hooks"] = new JsonObject { ["PreToolUse"] = new JsonArray(ForeignEntry("Bash", "echo keep-me").DeepClone()) },
        });
        var before = File.ReadAllText(path);

        var removed = await ManagedHooksWriter.RemoveAsync(path, block, CancellationToken.None);

        removed.ShouldBeFalse();
        File.ReadAllText(path).ShouldBe(before);
        File.Exists(path + ".bak").ShouldBeFalse();
    }

    [Fact]
    public async Task Remove_MixedEntry_DropsOnlyTheManagedCommandObject()
    {
        var path = ScratchPath();
        var block = BlockFor(McpClientId.Qwen);
        var mixed = new JsonObject
        {
            ["matcher"] = "Whatever",
            ["hooks"] = new JsonArray(
                new JsonObject { ["type"] = "command", ["command"] = "echo team-logger" },
                new JsonObject { ["type"] = "command", ["command"] = block.Command }),
        };
        WriteCanonical(path, new JsonObject
        {
            ["hooks"] = new JsonObject { ["PreToolUse"] = new JsonArray(mixed.DeepClone()) },
        });

        await UpsertAsync(path, block);
        await ManagedHooksWriter.RemoveAsync(path, block, CancellationToken.None);

        var entries = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!["PreToolUse"]!.AsArray();
        entries.Count.ShouldBe(1);
        entries[0]!["hooks"]!.AsArray().Count.ShouldBe(1);
        entries[0]!["hooks"]![0]!["command"]!.GetValue<string>().ShouldBe("echo team-logger");
    }

    [Fact]
    public async Task Upsert_HooksKeyIsForeignString_RefusesMergeWithoutRewriting()
    {
        var path = ScratchPath();
        File.WriteAllText(path, """{"hooks":"legacy"}""");
        var before = File.ReadAllText(path);

        await Should.ThrowAsync<InvalidDataException>(() =>
            UpsertAsync(path, BlockFor(McpClientId.Qwen)));

        File.ReadAllText(path).ShouldBe(before);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IsArmed_MissingForeignAndArmedDocuments_AnswerThreeStates(bool create, bool arm)
    {
        var path = ScratchPath();
        var block = BlockFor(McpClientId.Qwen);
        if (create)
        {
            WriteCanonical(path, new JsonObject { ["theme"] = arm ? "dark" : "light" });
            if (arm)
            {
                await UpsertAsync(path, block);
            }
        }

        (await ManagedHooksWriter.IsArmedAsync(path, block, CancellationToken.None)).ShouldBe(arm);
    }

    [Fact]
    public async Task IsArmed_CorruptJsonAnswersSilentNotArmed()
    {
        var path = ScratchPath();
        File.WriteAllText(path, "{ not json");

        (await ManagedHooksWriter.IsArmedAsync(path, BlockFor(McpClientId.Qwen), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Install_QwenUserScope_ArmsHookIntoSharedSettingsFile_KeepsForeignServerBytes()
    {
        var (service, environment, home) = NewService();
        var settingsPath = Path.Combine(home, ".qwen", "settings.json");
        WriteCanonical(settingsPath, new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["other"] = new JsonObject { ["command"] = "other-cmd" } },
        });
        var foreignBefore = JsonNode.Parse(File.ReadAllText(settingsPath))!["mcpServers"]!["other"]!
            .ToJsonString(CanonicalOptions);
        var client = McpClientRegistry.Get(McpClientId.Qwen);

        var result = await service.InstallAsync(client, McpScope.User, watch: false, writeInstructions: false, environment, CancellationToken.None);

        result.Error.ShouldBeNull();
        result.HooksArmed.ShouldBeTrue();
        var root = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        root["mcpServers"]!["other"]!.ToJsonString(CanonicalOptions).ShouldBe(foreignBefore);
        root["mcpServers"]!.AsObject().ContainsKey("squeezr").ShouldBeTrue();
        var block = HooksBlock.ForClient(client, environment.CommandPath)!;
        ManagedEntryCount(settingsPath, block).ShouldBe(1);
        var armedEntry = ManagedPreToolEntries(settingsPath).Single(entry => EntryCarriesManaged(entry, block));
        armedEntry!["matcher"]!.GetValue<string>().ShouldBe($"^{HookPreReadToolNames.PayloadIdFor("qwen")}$");
    }

    [Fact]
    public async Task Install_ClaudeUserScopeViaClientCli_WritesSettingsFileAndNeverOpensClaudeJson()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var binDir = TestDirs.NewTempDir();
        FakeCli.Install(binDir, "claude");
        var claudeJsonPath = Path.Combine(home, ".claude.json");
        File.WriteAllText(claudeJsonPath, """{"mcpServers":{}}""");
        var before = File.ReadAllBytes(claudeJsonPath);
        var environment = new McpOnboardingEnvironment(home, workspace, Path.Combine(home, "bin", "squeezr"), null);
        var service = new McpOnboardingService(new ClientCliLocator(binDir));
        var client = McpClientRegistry.Get(McpClientId.Claude);

        var result = await service.InstallAsync(client, McpScope.User, watch: true, writeInstructions: false, environment, CancellationToken.None);

        result.Method.ShouldBe("cli");
        File.ReadAllBytes(claudeJsonPath).ShouldBe(before);
        var settingsPath = Path.Combine(home, ".claude", "settings.json");
        JsonNode.Parse(File.ReadAllText(settingsPath))!["hooks"]!["PreToolUse"]!.AsArray().Count.ShouldBe(1);
        var block = HooksBlock.ForClient(client, environment.CommandPath)!;
        (await ManagedHooksWriter.IsArmedAsync(settingsPath, block, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task InstallTwice_QwenUserScope_ManagedEntryStaysExactlyOne()
    {
        var (service, environment, home) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Qwen);

        await service.InstallAsync(client, McpScope.User, watch: false, writeInstructions: false, environment, CancellationToken.None);
        var first = File.ReadAllText(Path.Combine(home, ".qwen", "settings.json"));
        await service.InstallAsync(client, McpScope.User, watch: false, writeInstructions: false, environment, CancellationToken.None);

        var settingsPath = Path.Combine(home, ".qwen", "settings.json");
        File.ReadAllText(settingsPath).ShouldBe(first);
        ManagedEntryCount(settingsPath, HooksBlock.ForClient(client, environment.CommandPath)!).ShouldBe(1);
    }

    [Fact]
    public async Task Remove_KimiUserScope_WritesNoHooksSideEffect()
    {
        var (service, environment, home) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Kimi);

        var install = await service.InstallAsync(client, McpScope.User, watch: false, writeInstructions: false, environment, CancellationToken.None);
        var remove = await service.RemoveAsync(client, McpScope.User, environment, CancellationToken.None);

        install.HooksArmed.ShouldBeFalse();
        remove.HooksRemoved.ShouldBeFalse();
        File.Exists(Path.Combine(home, ".kimi-code", "settings.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task E2E_QwenInstallDoctorRemove_ReturnsConfigToPreInstallBytes()
    {
        var (service, environment, home) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Qwen);
        var settingsPath = Path.Combine(home, ".qwen", "settings.json");
        var foreignEntry = ForeignEntry("Bash", "echo keep-me");
        WriteCanonical(settingsPath, new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["other"] = new JsonObject { ["command"] = "other-cmd" } },
            ["hooks"] = new JsonObject { ["PreToolUse"] = new JsonArray(foreignEntry.DeepClone()) },
            ["theme"] = "dark",
        });
        var preImage = File.ReadAllText(settingsPath);
        var foreignBefore = foreignEntry.ToJsonString(CanonicalOptions);

        await service.InstallAsync(client, McpScope.User, watch: false, writeInstructions: false, environment, CancellationToken.None);
        var armed = await service.DoctorAsync(client, environment, CancellationToken.None);
        armed.HookStatus.ShouldBe(HooksBlock.ArmedState);
        var remove = await service.RemoveAsync(client, McpScope.User, environment, CancellationToken.None);
        var disarmed = await service.DoctorAsync(client, environment, CancellationToken.None);

        remove.HooksRemoved.ShouldBeTrue();
        disarmed.HookStatus.ShouldBe(HooksBlock.NotArmedState);
        File.ReadAllText(settingsPath).ShouldBe(preImage);
        JsonNode.Parse(File.ReadAllText(settingsPath))!["hooks"]!["PreToolUse"]!.AsArray()
            .Count(entry => entry!.ToJsonString(CanonicalOptions) == foreignBefore).ShouldBe(1);
    }

    [Fact]
    public void ArmedPatterns_EqualTheDerivedSharedHomeIds()
    {
        foreach (var id in new[] { McpClientId.Qwen, McpClientId.Claude })
        {
            var runtime = id.ToString().ToLowerInvariant();
            var payloadId = HookPreReadToolNames.PayloadIdFor(runtime);
            payloadId.ShouldNotBeNull($"{runtime} is armed, so its payload id must live in the shared home");
            HookPreReadToolNames.IsReadTool(payloadId).ShouldBeTrue();
            var matcher = HooksBlock.ForClient(McpClientRegistry.Get(id), "/opt/squeezr/squeezr")!.Matcher;
            matcher.TrimStart('^').TrimEnd('$').ShouldBe(payloadId);
        }
    }

    [Fact]
    public void AcceptedNamesAndArmedRuntimes_StayPartitionedPerRuntime()
    {
        foreach (var identity in HookPreReadToolNames.Identities)
        {
            McpClientRegistry.TryGet(identity.Runtime, out var owner)
                .ShouldBeTrue($"'{identity.ToolId}' names unknown runtime '{identity.Runtime}'");
            var block = HooksBlock.ForClient(owner, "/opt/squeezr/squeezr");
            block.ShouldNotBeNull(
                $"'{identity.ToolId}' is accepted for {identity.Runtime} but that runtime is never armed");
            if (identity.DeliveredAsPayload)
            {
                block!.Matcher.TrimStart('^').TrimEnd('$').ShouldBe(identity.ToolId);
            }
        }
        foreach (var client in McpClientRegistry.All)
        {
            var armed = HooksBlock.ForClient(client, "/opt/squeezr/squeezr") is not null;
            armed.ShouldBe(client.HooksPathResolver is not null,
                $"{client.Id} must be armed by resolver and payload id together");
            if (!armed)
            {
                HookPreReadToolNames.PayloadIdFor(client.Id.ToString().ToLowerInvariant())
                    .ShouldBeNull($"{client.Id} is un-armed - arming it needs a measured hook layout first");
            }
        }
    }

    private static string ScratchPath() => Path.Combine(TestDirs.NewTempDir(), "settings.json");

    private static Task<bool> UpsertAsync(string path, HooksBlock block) =>
        ManagedHooksWriter.UpsertAsync(path, block, CancellationToken.None);

    private static HooksBlock BlockFor(McpClientId id) =>
        HooksBlock.ForClient(McpClientRegistry.Get(id), "/opt/squeezr/bin/squeezr")!;

    private static void WriteCanonical(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(CanonicalOptions));
    }

    private static JsonArray ManagedPreToolEntries(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!["hooks"]!["PreToolUse"]!.AsArray();

    private static bool EntryCarriesManaged(JsonNode? entry, HooksBlock block) =>
        entry is JsonObject obj && obj["hooks"] is JsonArray commands &&
        commands.Any(command => EntryCommandIsManaged(command, block));

    private static bool EntryCommandIsManaged(JsonNode? command, HooksBlock block) =>
        command is JsonObject value &&
        value.TryGetPropertyValue("command", out var node) &&
        node is JsonValue text && text.TryGetValue(out string? commandText) &&
        commandText is not null && block.Owns(commandText);

    private static int ManagedEntryCount(string path, HooksBlock block)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (root["hooks"] is not JsonObject hooks || hooks["PreToolUse"] is not JsonArray entries)
        {
            return 0;
        }
        return entries.Count(entry => EntryCarriesManaged(entry, block));
    }

    private static JsonObject ForeignEntry(string matcher, string command) => new()
    {
        ["matcher"] = matcher,
        ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command }),
    };

    private static (McpOnboardingService Service, McpOnboardingEnvironment Environment, string Home) NewService()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var environment = new McpOnboardingEnvironment(home, workspace, Path.Combine(home, "bin", "squeezr"), null);
        return (new McpOnboardingService(new ClientCliLocator(string.Empty)), environment, home);
    }
}

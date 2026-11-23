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

    [Theory]
    [InlineData("/opt/squeezr/bin/squeezr hook pre-read", true)]
    [InlineData("squeezr hook pre-read", true)]
    [InlineData("squeezr.exe hook pre-read", true)]
    [InlineData("\"squeezr.exe\" hook pre-read", true)]
    [InlineData("/x/y/squeezr hook pre-read", true)]
    [InlineData("Squeezr hook pre-read", true)]
    [InlineData("other-tool hook pre-read", false)]
    [InlineData("squeezr hook post-edit", false)]
    // The double-space form is NOT managed — a hand edit that swallows the separator reads as a foreign
    // entry and a re-install would add a second hook. Pinned as-is; changing it is a deliberate design act.
    [InlineData("squeezr  hook pre-read", false)]
    public void Owns_AcceptsTheReadmeSnippetFormsAndRejectsNeighbours(string candidate, bool expected)
    {
        BlockFor(McpClientId.Qwen).Owns(candidate).ShouldBe(expected);
    }

    [Fact]
    public async Task Upsert_SecondEventBlock_LivesBesidePreToolUse_RemoveRestoresBytes()
    {
        var path = ScratchPath();
        var preRead = BlockFor(McpClientId.Qwen);
        await UpsertAsync(path, preRead);
        var afterPreRead = File.ReadAllText(path);
        var sessionStart = new HooksBlock("SessionStart", "startup", "session", "/opt/squeezr/bin/squeezr hook session");

        (await UpsertAsync(path, sessionStart)).ShouldBeTrue();

        var hooks = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!.AsObject();
        hooks.ContainsKey("PreToolUse").ShouldBeTrue();
        hooks.ContainsKey("SessionStart").ShouldBeTrue();
        ManagedPreToolEntries(path).Count(entry => EntryCarriesManaged(entry, preRead)).ShouldBe(1);

        (await ManagedHooksWriter.RemoveAsync(path, sessionStart, CancellationToken.None)).ShouldBeTrue();
        File.ReadAllText(path).ShouldBe(afterPreRead);
        (await ManagedHooksWriter.IsArmedAsync(path, preRead, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public void ArmsFor_ArmableRuntimes_ReturnPreReadThenPostEdit()
    {
        foreach (var id in new[] { McpClientId.Qwen, McpClientId.Claude })
        {
            var arms = HooksBlock.ArmsFor(McpClientRegistry.Get(id), "/opt/squeezr/bin/squeezr");
            arms.Count.ShouldBe(2);
            arms[0].Event.ShouldBe("PreToolUse");
            arms[0].CommandToken.ShouldBe("hook pre-read");
            arms[1].Event.ShouldBe("PostToolUse");
            arms[1].CommandToken.ShouldBe("hook post-edit");
            arms[1].Command.ShouldBe("/opt/squeezr/bin/squeezr hook post-edit");
        }
        HooksBlock.ArmsFor(McpClientRegistry.Get(McpClientId.Kimi), "/opt/squeezr/bin/squeezr").ShouldBeEmpty();
        HooksBlock.ArmsFor(McpClientRegistry.Get(McpClientId.Codex), "/opt/squeezr/bin/squeezr").ShouldBeEmpty();
    }

    // Every id below is quoted with where it was measured, per the feature ruling that matcher ids come
    // from payloads and never from the spec table:
    // - qwen "write_file": two qwen-code.tool_call telemetry rows with "function_name":"write_file"
    //   (2026-11-11, real armed-block session).
    // - qwen "edit": live Qwen Code session transcripts on this box (~/.qwen/projects/*, counted
    //   2026-11-13: 5860 "function_name":"edit" telemetry rows in the Squeezr project corpus alone).
    // - qwen "notebook_edit": the live Qwen Code runtime's own tool schema (session 2026-11-13 declares
    //   notebook_edit); zero transcript occurrences because no notebook was ever edited — declared in
    //   task obstacles, armed anyway because the shipped T1 door accepts the id.
    // - claude "Edit"/"Write"/"NotebookEdit": live Claude Code transcripts on this box
    //   (~/.claude/projects/*/*.jsonl tool_use names, counted 2026-11-13: 200/65/34 rows).
    // - claude "MultiEdit": zero occurrences in the 1333-file local transcript corpus; the id rides the
    //   T1-shipped HookPostEditToolNames table (reviewed carrier) — declared in task obstacles.
    // - qwen pre-read "^read_file$": the block 114 actually wrote, pinned verbatim below as the
    //   upgrade compatibility shape.
    [Fact]
    public void PostEditArmMatchers_OverMeasuredPayloadIds_PerRuntimeShape()
    {
        HooksBlock.PostEditForClient(McpClientRegistry.Get(McpClientId.Qwen), "/opt/squeezr/bin/squeezr")!
            .Matcher.ShouldBe("^(edit|write_file|notebook_edit)$");
        HooksBlock.PostEditForClient(McpClientRegistry.Get(McpClientId.Claude), "/opt/squeezr/bin/squeezr")!
            .Matcher.ShouldBe("Edit|Write|MultiEdit|NotebookEdit");
    }

    [Fact]
    public void PreReadMatcher_KeepsTheExact114ArmedShape_ForByteStableUpgrade()
    {
        // The 114 block shipped on this box (armed-block-as-installed.json) carries "^read_file$";
        // an unparenthesized single id keeps a re-install over that config a byte-for-byte no-op.
        HooksBlock.ForClient(McpClientRegistry.Get(McpClientId.Qwen), "/opt/squeezr/bin/squeezr")!
            .Matcher.ShouldBe("^read_file$");
    }

    [Fact]
    public void PostEditIdentities_StayPartitionedPerRuntime_AndSurviveTheirMatcher()
    {
        foreach (var identity in HookPostEditToolNames.Identities)
        {
            McpClientRegistry.TryGet(identity.Runtime, out var owner)
                .ShouldBeTrue($"'{identity.ToolId}' names unknown runtime '{identity.Runtime}'");
            var arm = HooksBlock.PostEditForClient(owner, "/opt/squeezr/bin/squeezr");
            arm.ShouldNotBeNull(
                $"'{identity.ToolId}' is accepted for {identity.Runtime} but that runtime is never armed");
            var alternatives = identity.Runtime == "qwen"
                ? arm!.Matcher.TrimStart('^').Trim('$').Trim('(').Trim(')').Split('|')
                : arm!.Matcher.Split('|');
            alternatives.ShouldContain(identity.ToolId,
                $"'{identity.ToolId}' must fire its own hook — unanchored or missing matchers over-match or under-match");
            arm.CommandToken.ShouldBe("hook post-edit");
        }
    }

    [Fact]
    public async Task Upsert_BothArms_LiveUnderTheirEventKeys_AndSecondRunIsByteNoOp()
    {
        var path = ScratchPath();
        var arms = HooksBlock.ArmsFor(McpClientRegistry.Get(McpClientId.Qwen), "/opt/squeezr/bin/squeezr");

        (await UpsertAsync(path, arms[0])).ShouldBeTrue();
        (await UpsertAsync(path, arms[1])).ShouldBeTrue();
        var armed = File.ReadAllText(path);
        (await UpsertAsync(path, arms[0])).ShouldBeFalse();
        (await UpsertAsync(path, arms[1])).ShouldBeFalse();

        File.ReadAllText(path).ShouldBe(armed);
        var hooks = JsonNode.Parse(armed)!["hooks"]!.AsObject();
        ManagedEntryCount(path, arms[0]).ShouldBe(1);
        hooks["PostToolUse"]!.AsArray().Count(entry => EntryCarriesManaged(entry, arms[1])).ShouldBe(1);
    }

    [Fact]
    public async Task Upgrade_Legacy114OnlyConfig_GainsPostEdit_WithForeignAndPreReadBytesUntouched()
    {
        var path = ScratchPath();
        var legacy = new HooksBlock("PreToolUse", "^read_file$", "pre-read", "/opt/squeezr/bin/squeezr hook pre-read");
        var foreign = ForeignEntry("Bash", "echo keep-me");
        WriteCanonical(path, new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["other"] = new JsonObject { ["command"] = "other-cmd" } },
            ["hooks"] = new JsonObject { ["PreToolUse"] = new JsonArray(foreign.DeepClone(), legacy.ToJson()) },
            ["theme"] = "dark",
        });
        var preReadArrayBefore = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!["PreToolUse"]!
            .ToJsonString(CanonicalOptions);

        var current = HooksBlock.ForClient(McpClientRegistry.Get(McpClientId.Qwen), "/opt/squeezr/bin/squeezr")!;
        (await UpsertAsync(path, current)).ShouldBeFalse();
        var postEdit = HooksBlock.PostEditForClient(McpClientRegistry.Get(McpClientId.Qwen), "/opt/squeezr/bin/squeezr")!;
        (await UpsertAsync(path, postEdit)).ShouldBeTrue();

        var after = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        after["hooks"]!["PreToolUse"]!.ToJsonString(CanonicalOptions).ShouldBe(preReadArrayBefore);
        after["theme"]!.GetValue<string>().ShouldBe("dark");
        after["mcpServers"]!.AsObject().ContainsKey("other").ShouldBeTrue();
        after["hooks"]!.AsObject().ContainsKey("PostToolUse").ShouldBeTrue();
        (await ManagedHooksWriter.IsArmedAsync(path, postEdit, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Remove_BothArms_StripsEverything_LeavesForeignEntryBytesOnly()
    {
        var path = ScratchPath();
        var arms = HooksBlock.ArmsFor(McpClientRegistry.Get(McpClientId.Claude), "/opt/squeezr/bin/squeezr");
        var foreign = ForeignEntry("Edit", "echo foreign");
        WriteCanonical(path, new JsonObject
        {
            ["theme"] = "dark",
            ["hooks"] = new JsonObject { ["PreToolUse"] = new JsonArray(foreign.DeepClone()) },
        });

        await UpsertAsync(path, arms[0]);
        await UpsertAsync(path, arms[1]);
        (await ManagedHooksWriter.RemoveAsync(path, arms[1], CancellationToken.None)).ShouldBeTrue();
        (await ManagedHooksWriter.RemoveAsync(path, arms[0], CancellationToken.None)).ShouldBeTrue();

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["hooks"]!.AsObject().ContainsKey("PostToolUse").ShouldBeFalse();
        var entries = root["hooks"]!["PreToolUse"]!.AsArray();
        entries.Count.ShouldBe(1);
        entries[0]!.ToJsonString(CanonicalOptions).ShouldBe(foreign.ToJsonString(CanonicalOptions));
        root["theme"]!.GetValue<string>().ShouldBe("dark");
    }

    [Fact]
    public void PostEditBlock_OwnsItsTokenAndRejectsThePreReadToken()
    {
        var postEdit = HooksBlock.PostEditForClient(McpClientRegistry.Get(McpClientId.Qwen), "/opt/squeezr/bin/squeezr")!;
        postEdit.Owns("squeezr hook post-edit").ShouldBeTrue();
        postEdit.Owns("/x/y/squeezr.exe hook post-edit").ShouldBeTrue();
        postEdit.Owns("squeezr hook pre-read").ShouldBeFalse();
        postEdit.Owns("other-tool hook post-edit").ShouldBeFalse();
        HooksBlock.ForClient(McpClientRegistry.Get(McpClientId.Qwen), "/opt/squeezr/bin/squeezr")!
            .Owns("squeezr hook post-edit").ShouldBeFalse();
    }

    [Fact]
    public async Task ServiceInstall_OverLegacy114UserConfig_ArmsBoth_KeepsForeign_ReinstallIsNoOp()
    {
        var (service, environment, home) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Qwen);
        var settingsPath = Path.Combine(home, ".qwen", "settings.json");
        var legacy = HooksBlock.ForClient(client, environment.CommandPath)!;
        var foreign = ForeignEntry("Bash", "echo keep-me");
        WriteCanonical(settingsPath, new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["other"] = new JsonObject { ["command"] = "other-cmd" } },
            ["hooks"] = new JsonObject { ["PreToolUse"] = new JsonArray(foreign.DeepClone(), legacy.ToJson()) },
            ["theme"] = "dark",
        });

        var install = await service.InstallAsync(client, McpScope.User, watch: false, writeInstructions: false, environment, CancellationToken.None);
        var upgraded = File.ReadAllText(settingsPath);
        install.HooksArmed.ShouldBeTrue();
        install.PostEditHooksArmed.ShouldBeTrue();
        await service.InstallAsync(client, McpScope.User, watch: false, writeInstructions: false, environment, CancellationToken.None);

        File.ReadAllText(settingsPath).ShouldBe(upgraded);
        var root = JsonNode.Parse(upgraded)!.AsObject();
        root["hooks"]!.AsObject()["PreToolUse"]!.AsArray()
            .Count(entry => entry!.ToJsonString(CanonicalOptions) == foreign.ToJsonString(CanonicalOptions)).ShouldBe(1);
        root["theme"]!.GetValue<string>().ShouldBe("dark");
        var postEdit = HooksBlock.PostEditForClient(client, environment.CommandPath)!;
        ManagedEntryCount(settingsPath, postEdit, "PostToolUse").ShouldBe(1);

        var remove = await service.RemoveAsync(client, McpScope.User, environment, CancellationToken.None);
        remove.HooksRemoved.ShouldBeTrue();
        remove.PostEditHooksRemoved.ShouldBeTrue();
        var cleaned = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        File.ReadAllText(settingsPath).ShouldNotContain("hook post-edit");
        File.ReadAllText(settingsPath).ShouldNotContain("hook pre-read");
        cleaned["hooks"]!.AsObject()["PreToolUse"]!.AsArray()
            .Count(entry => entry!.ToJsonString(CanonicalOptions) == foreign.ToJsonString(CanonicalOptions)).ShouldBe(1);
        cleaned["theme"]!.GetValue<string>().ShouldBe("dark");
    }

    [Fact]
    public async Task ServiceDoctor_Legacy114Config_ReportsPreReadArmedAndPostEditNotArmed()
    {
        var (service, environment, home) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Qwen);
        var settingsPath = Path.Combine(home, ".qwen", "settings.json");
        WriteCanonical(settingsPath, new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray(HooksBlock.ForClient(client, environment.CommandPath)!.ToJson()),
            },
        });

        var transitional = await service.DoctorAsync(client, environment, CancellationToken.None);

        transitional.HookStatus.ShouldBe(HooksBlock.ArmedState);
        transitional.PostEditHookStatus.ShouldBe(HooksBlock.NotArmedState);
    }

    [Fact]
    public async Task ServiceInstall_ProjectScope_ArmsBothEvents_InProjectSettingsFile()
    {
        var (service, environment, _) = NewService();
        var client = McpClientRegistry.Get(McpClientId.Qwen);

        var install = await service.InstallAsync(client, McpScope.Project, watch: false, writeInstructions: false, environment, CancellationToken.None);
        var projectSettings = Path.Combine(environment.WorkspaceRoot, ".qwen", "settings.json");

        install.HooksArmed.ShouldBeTrue();
        install.PostEditHooksArmed.ShouldBeTrue();
        var postEdit = HooksBlock.PostEditForClient(client, environment.CommandPath)!;
        (await ManagedHooksWriter.IsArmedAsync(projectSettings, postEdit, CancellationToken.None)).ShouldBeTrue();
        var diagnostics = await service.DoctorAsync(client, environment, CancellationToken.None);
        diagnostics.HookStatus.ShouldBe(HooksBlock.ProjectArmedState);
        diagnostics.PostEditHookStatus.ShouldBe(HooksBlock.ProjectArmedState);
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

    private static int ManagedEntryCount(string path, HooksBlock block) => ManagedEntryCount(path, block, "PreToolUse");

    private static int ManagedEntryCount(string path, HooksBlock block, string eventKey)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (root["hooks"] is not JsonObject hooks || hooks[eventKey] is not JsonArray entries)
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

using System.Text.Json.Nodes;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp.Onboarding;

public sealed class McpOnboardingService
{
    private static readonly string[] TrustedProjectWarning =
    [
        "Codex loads project config only for trusted projects; make sure this workspace is trusted in Codex, otherwise the entry is ignored.",
    ];

    private const int HeadlessMessageCap = 200;

    private readonly ClientCliLocator _locator;
    private readonly ClientCliBridge _bridge;

    public McpOnboardingService(ClientCliLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        _locator = locator;
        _bridge = new ClientCliBridge(locator);
    }

    public async Task<McpInstallResult> InstallAsync(
        McpClientDescriptor client,
        McpScope scope,
        bool watch,
        bool writeInstructions,
        McpOnboardingEnvironment environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(environment);
        var configPath = McpClientPaths.ConfigPath(client, environment, scope);
        var instructionPath = McpClientPaths.InstructionPath(client, environment, scope);
        var decision = McpInstallStrategy.Resolve(client, scope, _locator);
        if (decision.Method == McpInstallMethod.Cli)
        {
            await InstallViaCliAsync(client, scope, environment.CommandPath, watch, cancellationToken);
        }
        else
        {
            var entry = ServerEntryBuilder.Build(client, environment.CommandPath, watch);
            await WriteEntryAsync(client, configPath, entry, cancellationToken);
        }
        var instructionsWritten = false;
        if (writeInstructions)
        {
            InstructionsWriter.Upsert(instructionPath);
            instructionsWritten = true;
        }
        return new McpInstallResult(
            ClientName(client),
            ScopeName(scope),
            MethodName(decision.Method),
            configPath,
            instructionsWritten,
            decision.Warning,
            null);
    }

    public async Task<McpRemoveResult> RemoveAsync(
        McpClientDescriptor client,
        McpScope scope,
        McpOnboardingEnvironment environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(environment);
        var configPath = McpClientPaths.ConfigPath(client, environment, scope);
        var instructionPath = McpClientPaths.InstructionPath(client, environment, scope);
        var decision = McpInstallStrategy.Resolve(client, scope, _locator);
        if (decision.Method == McpInstallMethod.Cli)
        {
            await _bridge.RemoveAsync(client, cancellationToken);
        }
        else
        {
            await RemoveEntryAsync(client, configPath, cancellationToken);
        }
        var instructionsRemoved = InstructionBlockExists(instructionPath);
        InstructionsWriter.Remove(instructionPath);
        return new McpRemoveResult(
            ClientName(client),
            ScopeName(scope),
            MethodName(decision.Method),
            configPath,
            instructionsRemoved,
            decision.Warning,
            null);
    }

    public async Task<McpClientDiagnostics> DoctorAsync(
        McpClientDescriptor client,
        McpOnboardingEnvironment environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(environment);
        var userDecision = McpInstallStrategy.Resolve(client, McpScope.User, _locator);
        var projectDecision = McpInstallStrategy.Resolve(client, McpScope.Project, _locator);
        var userConfigPath = McpClientPaths.ConfigPath(client, environment, McpScope.User);
        var projectConfigPath = McpClientPaths.ConfigPath(client, environment, McpScope.Project);
        var userEntry = await McpConfigProbe.HasServerAsync(userConfigPath, client.ConfigFormat, ClientCliBridge.ServerName, cancellationToken);
        var projectEntry = await McpConfigProbe.HasServerAsync(projectConfigPath, client.ConfigFormat, ClientCliBridge.ServerName, cancellationToken);
        var userCommand = await McpConfigProbe.TryGetServerCommandAsync(userConfigPath, client.ConfigFormat, ClientCliBridge.ServerName, cancellationToken);
        var projectCommand = await McpConfigProbe.TryGetServerCommandAsync(projectConfigPath, client.ConfigFormat, ClientCliBridge.ServerName, cancellationToken);
        string? expectedPath = null;
        try
        {
            expectedPath = StableCommandPath.Resolve(environment.HomeDir, Environment.ProcessPath);
        }
        catch (InvalidOperationException)
        {
        }
        var userStale = userEntry && userCommand is not null && expectedPath is not null && !PathsMatch(userCommand, expectedPath);
        var projectStale = projectEntry && projectCommand is not null && expectedPath is not null && !PathsMatch(projectCommand, expectedPath);
        var indexState = WorkspaceState(environment.WorkspaceRoot, environment.SessionDirectory ?? environment.WorkspaceRoot);
        var watch = StoreWatchProbe.TryProbe(environment.WorkspaceRoot);
        var warnings = new List<string>();
        var userWarning = userDecision.Warning;
        if (userWarning is not null)
        {
            warnings.Add(userWarning);
        }
        if (projectDecision.Warning is { } projectWarning && !string.Equals(projectWarning, userWarning, StringComparison.Ordinal))
        {
            warnings.Add(projectWarning);
        }
        if (client.Id == McpClientId.Codex && projectEntry)
        {
            warnings.AddRange(TrustedProjectWarning);
        }
        return new McpClientDiagnostics(
            ClientName(client),
            ClientDetector.IsInstalled(client, environment, _locator),
            userEntry,
            projectEntry,
            MethodName(userDecision.Method),
            MethodName(projectDecision.Method),
            _locator.Locate(ClientCliBridge.ServerName) is not null,
            indexState is null,
            await ProbeHeadlessAsync(client, cancellationToken),
            warnings,
            userCommand,
            projectCommand,
            userStale,
            projectStale,
            expectedPath,
            indexState,
            watch);
    }

    private static ToolErrorPayload? WorkspaceState(string workspaceRoot, string sessionDirectory)
    {
        var workspace = new McpWorkspace(
            workspaceRoot,
            sessionDirectory,
            StartupConfig.TryRead(workspaceRoot)?.Embeddings.Provider);
        if (!workspace.IsWorkspace)
        {
            return ToolErrors.PayloadFor(workspace.NotInitializedException(), workspace);
        }

        if (!File.Exists(SqueezrPaths.TrigramIndexPath(workspaceRoot)))
        {
            return ToolErrors.PayloadFor(
                new WorkspaceException(RemedyText.IndexNotBuilt, WorkspaceException.IndexNotBuiltCode),
                workspace);
        }

        return null;
    }

    private async Task<string?> ProbeHeadlessAsync(McpClientDescriptor client, CancellationToken cancellationToken)
    {
        if (_locator.Locate(client.BinaryName) is null)
        {
            return null;
        }
        try
        {
            var output = await _bridge.ListAsync(client, cancellationToken);
            return output.Contains(ClientCliBridge.ServerName, StringComparison.OrdinalIgnoreCase)
                ? "registered"
                : "not registered";
        }
        catch (Exception ex) when (ex is ClientCliException or TimeoutException or IOException or InvalidOperationException)
        {
            return $"unavailable: {CollapseToOneLine(ex.Message)}";
        }
    }

    private static string CollapseToOneLine(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= HeadlessMessageCap
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, HeadlessMessageCap - 3), "...");
    }

    private async Task InstallViaCliAsync(
        McpClientDescriptor client,
        McpScope scope,
        string commandPath,
        bool watch,
        CancellationToken cancellationToken)
    {
        try
        {
            await _bridge.AddAsync(client, scope, commandPath, watch, cancellationToken);
        }
        catch (ClientCliException ex) when (IsAlreadyExistsError(ex))
        {
            await _bridge.RemoveAsync(client, cancellationToken);
            await _bridge.AddAsync(client, scope, commandPath, watch, cancellationToken);
        }
    }

    private static bool IsAlreadyExistsError(ClientCliException ex) =>
        ex.Stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteEntryAsync(McpClientDescriptor client, string configPath, JsonObject entry, CancellationToken cancellationToken)
    {
        if (client.ConfigFormat == McpConfigFormat.Json)
        {
            await JsonMcpConfigWriter.InstallAsync(configPath, ClientCliBridge.ServerName, entry, cancellationToken);
        }
        else
        {
            await TomlMcpConfigWriter.InstallAsync(configPath, ClientCliBridge.ServerName, entry, cancellationToken);
        }
    }

    private static async Task RemoveEntryAsync(McpClientDescriptor client, string configPath, CancellationToken cancellationToken)
    {
        if (client.ConfigFormat == McpConfigFormat.Json)
        {
            await JsonMcpConfigWriter.RemoveAsync(configPath, ClientCliBridge.ServerName, cancellationToken);
        }
        else
        {
            await TomlMcpConfigWriter.RemoveAsync(configPath, ClientCliBridge.ServerName, cancellationToken);
        }
    }

    private static bool InstructionBlockExists(string instructionPath) =>
        File.Exists(instructionPath) &&
        File.ReadAllText(instructionPath).Contains(SqueezrInstructions.StartMarker, StringComparison.Ordinal);

    private static string ClientName(McpClientDescriptor client) => client.Id.ToString().ToLowerInvariant();

    private static string ScopeName(McpScope scope) => scope.ToString().ToLowerInvariant();

    private static string MethodName(McpInstallMethod method) => method.ToString().ToLowerInvariant();

    internal static bool PathsMatch(string a, string b)
    {
        var normA = NormalizePath(a);
        var normB = NormalizePath(b);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(normA, normB, comparison);
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

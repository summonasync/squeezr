using System.Text.Json.Nodes;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Mcp.Onboarding;

public sealed class McpOnboardingService
{
    private static readonly string[] TrustedProjectWarning =
    [
        "Codex loads project config only for trusted projects; make sure this workspace is trusted in Codex, otherwise the entry is ignored.",
    ];

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
            await _bridge.AddAsync(client, scope, environment.CommandPath, watch, cancellationToken);
        }
        else
        {
            var entry = ServerEntryBuilder.Build(client, environment.WorkspaceRoot, environment.CommandPath, watch);
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
        var userEntry = await McpConfigProbe.HasServerAsync(
            McpClientPaths.ConfigPath(client, environment, McpScope.User),
            client.ConfigFormat,
            ClientCliBridge.ServerName,
            cancellationToken);
        var projectEntry = await McpConfigProbe.HasServerAsync(
            McpClientPaths.ConfigPath(client, environment, McpScope.Project),
            client.ConfigFormat,
            ClientCliBridge.ServerName,
            cancellationToken);
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
            File.Exists(SqueezrPaths.MetaDbPath(environment.WorkspaceRoot)),
            await ProbeHeadlessAsync(client, cancellationToken),
            warnings);
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
            return $"unavailable: {ex.Message}";
        }
    }

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
}

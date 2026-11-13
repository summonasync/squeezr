using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Infra;
using Squeezr.Cli.Output;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Health;
using Squeezr.Core.Infra;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class DoctorCommand : AsyncCommand<DoctorCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    public sealed record DoctorJsonLine(string Kind, bool Healthy, IReadOnlyList<DoctorFinding> Checks);

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var utcNow = DateTime.UtcNow;
        var findings = new List<DoctorFinding>(DoctorRunner
            .CollectWorkspaceFindings(
                utcNow,
                semanticFacts: root => SemanticFacts(root, cancellationToken).GetAwaiter().GetResult())
            .Findings);
        findings.AddRange(await ServiceFindings(cancellationToken));

        var report = DoctorRunner.Report(findings);
        if (settings.Json)
        {
            JsonOutput.WriteLine(new DoctorJsonLine("doctor", report.Healthy, report.Findings));
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold]squeezr doctor[/] — {utcNow:yyyy-MM-dd HH:mm} UTC");
            foreach (var finding in findings)
            {
                var state = finding.State == DoctorRunner.StateFail ? "[red]fail[/]" : "[grey]info[/]";
                var remedy = finding.Remedy is null ? string.Empty : $" [grey]→ {finding.Remedy.EscapeMarkup()}[/]";
                AnsiConsole.MarkupLine(
                    $"{state} {finding.Name.EscapeMarkup()}: {finding.Detail.EscapeMarkup()}{remedy}");
            }

            AnsiConsole.MarkupLine(report.Healthy
                ? "[green]healthy[/] — no failures found"
                : $"[yellow]{findings.Count(f => f.State == DoctorRunner.StateFail)} failure(s)[/] — remedies above, nothing was changed");
        }

        return report.Healthy ? 0 : 1;
    }

    private static async Task<WorkspaceSemanticFacts> SemanticFacts(string workspaceRoot, CancellationToken cancellationToken)
    {
        try
        {
            var warnings = EmbeddingRuntimeFactory.TryCreate(
                workspaceRoot, withProvider: false, out _, out var vectorStore);
            var health = await EmbeddingsHealthProbe.ProbeAsync(workspaceRoot, cancellationToken);
            if (vectorStore is null)
            {
                return new WorkspaceSemanticFacts(
                    null, health, warnings.Count > 0 ? string.Join("; ", warnings) : "vector store unavailable");
            }

            QdrantStatus qdrant;
            using (var meta = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot)))
            {
                qdrant = await QdrantStatusProbe.ProbeAsync(vectorStore, meta, cancellationToken);
                health = EmbeddingVintages.Measure(health, meta);
            }

            return new WorkspaceSemanticFacts(qdrant, health);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return WorkspaceSemanticFacts.Silent;
        }
    }

    private static async Task<IReadOnlyList<DoctorFinding>> ServiceFindings(CancellationToken cancellationToken)
    {
        var findings = new List<DoctorFinding>();
        var legacyPath = ServicesComposeFile.ComposeFilePath;
        var ownedPath = OwnedComposeFile.FilePath;

        if (!File.Exists(legacyPath) && !File.Exists(ownedPath))
        {
            findings.Add(new DoctorFinding(
                "services", DoctorRunner.StateFail,
                $"no compose file at {legacyPath}", DoctorRemedies.ServicesUp));
            return findings;
        }

        try
        {
            var cli = new DockerComposeCli(new ProcessRunner());
            var installation = await cli.DetectAsync(cancellationToken);

            if (File.Exists(legacyPath))
            {
                await CollectProjectFindings(
                    findings, cli, installation, legacyPath, ServicesComposeFile.ProjectName, "services", cancellationToken);
            }

            if (File.Exists(ownedPath))
            {
                await CollectProjectFindings(
                    findings, cli, installation, ownedPath, OwnedComposeFile.ProjectName, "owned qdrant", cancellationToken);
            }
        }
        catch (ServicesException ex)
        {
            findings.Add(new DoctorFinding(
                "services", DoctorRunner.StateFail, ex.Message, DoctorRemedies.ServicesUp));
        }

        return findings;
    }

    private static async Task CollectProjectFindings(
        List<DoctorFinding> findings,
        DockerComposeCli cli,
        DockerComposeInstallation installation,
        string composePath,
        string projectName,
        string label,
        CancellationToken cancellationToken)
    {
        var result = await cli.RunCheckedAsync(
            installation, composePath, ["ps", "--format", "json"], DockerComposeCli.StatusTimeout,
            cancellationToken, projectName: projectName);
        var states = DockerComposeCli.ParseServiceStates(result.StdOut, installation.Version);
        foreach (var state in states)
        {
            if (string.Equals(state.State, "running", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new DoctorFinding(
                    $"{label} {state.Service}", DoctorRunner.StateInfo,
                    $"running ({state.Status})", null));
            }
            else
            {
                findings.Add(new DoctorFinding(
                    $"{label} {state.Service}", DoctorRunner.StateFail,
                    $"state: {state.State}", DoctorRemedies.ServicesUp));
            }
        }
    }
}

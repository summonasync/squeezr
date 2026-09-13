using Spectre.Console.Cli;

namespace Squeezr.Cli.Commands;

public static class McpCommandRegistration
{
    public static void AddMcpBranch(IConfigurator config)
    {
        config.AddBranch("mcp", branch =>
        {
            branch.SetDescription("Connect AI agent CLIs to the squeezr MCP server");
            branch.AddCommand<McpInstallCommand>("install")
                .WithDescription("Register the squeezr MCP server with agent CLIs");
            branch.AddCommand<McpRemoveCommand>("remove")
                .WithDescription("Remove the squeezr MCP server from agent CLIs");
            branch.AddCommand<McpDoctorCommand>("doctor")
                .WithDescription("Report squeezr MCP registration status for agent CLIs");
        });
    }
}

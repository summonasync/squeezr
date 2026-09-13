namespace Squeezr.Cli.Mcp.Onboarding;

public static class McpConfigBackup
{
    public static void Create(string configPath)
    {
        if (File.Exists(configPath))
        {
            File.Copy(configPath, configPath + ".bak", overwrite: true);
        }
    }
}

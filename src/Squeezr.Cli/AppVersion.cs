using System.Reflection;

namespace Squeezr.Cli;

public static class AppVersion
{
    public static string Current =>
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}

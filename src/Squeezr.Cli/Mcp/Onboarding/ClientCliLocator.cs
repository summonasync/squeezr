namespace Squeezr.Cli.Mcp.Onboarding;

public sealed class ClientCliLocator
{
    private static readonly string[] FallbackWindowsExtensions = [".com", ".exe", ".bat", ".cmd"];

    private readonly string _pathValue;

    public ClientCliLocator()
        : this(Environment.GetEnvironmentVariable("PATH"))
    {
    }

    public ClientCliLocator(string? pathValue)
    {
        _pathValue = pathValue ?? string.Empty;
    }

    public string? Locate(string binaryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryName);
        foreach (var directory in EnumeratePathDirectories())
        {
            var candidate = Path.Combine(directory, binaryName);
            if (IsExecutable(candidate))
            {
                return candidate;
            }
            if (OperatingSystem.IsWindows())
            {
                foreach (var extension in EnumerateWindowsExtensions(binaryName))
                {
                    var withExtension = candidate + extension;
                    if (IsExecutable(withExtension))
                    {
                        return withExtension;
                    }
                }
            }
        }
        return null;
    }

    private IEnumerable<string> EnumeratePathDirectories() =>
        _pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> EnumerateWindowsExtensions(string binaryName)
    {
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = string.IsNullOrEmpty(pathExt)
            ? FallbackWindowsExtensions
            : pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var extension in extensions)
        {
            var normalized = extension.StartsWith('.') ? extension : "." + extension;
            if (!Path.HasExtension(binaryName) ||
                !binaryName.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                yield return normalized;
            }
        }
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            if (OperatingSystem.IsWindows())
            {
                return true;
            }
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

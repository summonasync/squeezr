using System.Text.RegularExpressions;

namespace Squeezr.Cli.Infra;

public static class DockerComposeVersion
{
    private static readonly Regex VersionPattern = new(@"\d+\.\d+\.\d+", RegexOptions.None, RegexTimeout);

    private static TimeSpan RegexTimeout => TimeSpan.FromMilliseconds(100);

    public static Version? TryParse(string output)
    {
        var match = VersionPattern.Match(output ?? string.Empty);
        return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
    }
}

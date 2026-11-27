using Squeezr.Lite.Output;

namespace Squeezr.Lite.Commands;

internal static class RegistrationAdvisoryOutput
{
    public static void Write(IReadOnlyList<RegistrationAdvisory> advisories, bool json)
    {
        if (json)
        {
            if (advisories.Count > 0)
            {
                JsonOutput.WriteWarnings(
                    [.. advisories.Select(advisory => new JsonWarningBody(advisory.Code, advisory.Message))]);
            }

            return;
        }

        foreach (var advisory in advisories)
        {
            Console.Error.WriteLine($"warning: {advisory.Message}");
        }
    }
}

using System.Globalization;

namespace Squeezr.Cli.Output;

public static class StoreSizeText
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} B"
            : value.ToString("0.0", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}

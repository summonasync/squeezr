using System.Globalization;

namespace Squeezr.Core.Indexing.Watch.Supervision;

public sealed record OwnBinaryDrift(string BinaryPath, DateTime ProcessStartUtc, DateTime BinaryModifiedUtc)
{
    public string Reason =>
        $"watchd is retiring after an upgrade: {BinaryPath} changed at {Stamp(BinaryModifiedUtc)}, " +
        $"this process started at {Stamp(ProcessStartUtc)}";

    private static string Stamp(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

namespace Squeezr.Core.Indexing.Query;

public sealed record FusionTuning
{
    public int? K
    {
        get;
        init => field = value is < 1
            ? throw new ArgumentOutOfRangeException(nameof(value), value, "K must be positive")
            : value;
    }

    public double ExactWeight
    {
        get;
        init => field = value < 0
            ? throw new ArgumentOutOfRangeException(nameof(value), value, "weight must not be negative")
            : value;
    } = 1.0;

    public double SemanticWeight
    {
        get;
        init => field = value < 0
            ? throw new ArgumentOutOfRangeException(nameof(value), value, "weight must not be negative")
            : value;
    } = 1.0;
}

namespace Squeezr.Core.Storage;

public sealed record OrphanInfo(string StoreKey, string Name, string? RootPath)
{
    public bool IsAdoptable => RootPath is not null;
}

namespace Squeezr.Semantic.Infra;

public sealed class RocmDeviceProbe : IRocmDeviceProbe
{
    public const string KfdDevicePath = "/dev/kfd";
    public const string DriDevicePath = "/dev/dri";

    private readonly string _kfdDevicePath;
    private readonly string _driDevicePath;

    public RocmDeviceProbe()
        : this(KfdDevicePath, DriDevicePath)
    {
    }

    public RocmDeviceProbe(string kfdDevicePath, string driDevicePath)
    {
        _kfdDevicePath = kfdDevicePath;
        _driDevicePath = driDevicePath;
    }

    public bool AreAvailable() => File.Exists(_kfdDevicePath) && Directory.Exists(_driDevicePath);
}

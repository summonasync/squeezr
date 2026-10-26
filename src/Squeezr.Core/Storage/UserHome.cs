namespace Squeezr.Core.Storage;

public static class UserHome
{
    // DoNotVerify keeps a dangling HOME (set, gone) a real path, so callers fail against the
    // location the environment believes in; the verifying read would blank it, and a blank then
    // cannot be told apart from HOME being unset. A null here genuinely means "no user home".
    public static string? Path =>
        NonBlank(Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify));

    private static string? NonBlank(string home) => string.IsNullOrWhiteSpace(home) ? null : home;
}

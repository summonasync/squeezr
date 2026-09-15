using System.Security.Cryptography;
using System.Text;

namespace Squeezr.Core.Storage;

public static class WorkspaceStoreKeys
{
    public static string Create(string workspaceName, string canonicalRootPath)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(canonicalRootPath)));
        return $"{Slug(workspaceName)}-{hash[..8].ToLowerInvariant()}";
    }

    public static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        var pendingSeparator = false;
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(c);
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.Length > 0 ? builder.ToString() : "workspace";
    }
}

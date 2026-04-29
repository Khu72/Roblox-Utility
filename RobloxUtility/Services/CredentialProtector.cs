using System.Security.Cryptography;
using System.Text;

namespace RobloxUtility.Services;

public static class CredentialProtector
{
    public static string ProtectToBase64(string plain, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(plain);
        return Convert.ToBase64String(ProtectedData.Protect(bytes, null, scope));
    }

    public static string? UnprotectFromBase64(string? b64, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        if (string.IsNullOrWhiteSpace(b64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(b64);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, scope));
        }
        catch
        {
            return null;
        }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Clouder.Email;

public static class CredentialProtector
{
    public static byte[] Protect(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);
        return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
    }

    public static string Unprotect(byte[] protectedData)
    {
        var bytes = ProtectedData.Unprotect(protectedData, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}

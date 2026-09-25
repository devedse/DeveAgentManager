using Microsoft.AspNetCore.DataProtection;

namespace DeveAgentManager.Core.Security;

/// <summary>Encrypts secrets stored in the database. The keys live in /data/keys.</summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("DeveAgentManager.Secrets.v1");

    public string? Protect(string? value) => string.IsNullOrEmpty(value) ? null : _protector.Protect(value);

    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Happens when /data/keys was lost or replaced.
            return null;
        }
    }
}

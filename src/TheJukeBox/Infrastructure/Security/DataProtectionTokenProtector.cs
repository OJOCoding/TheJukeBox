using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using TheJukeBox.Diagnostics;

namespace TheJukeBox.Infrastructure.Security;

/// <summary>
/// <see cref="ITokenProtector"/> backed by the ASP.NET Core Data Protection stack.
/// </summary>
/// <remarks>
/// Data Protection rather than hand-rolled AES because it handles key generation,
/// rotation, and the authenticated-encryption details that are easy to get subtly
/// wrong. The key ring is persisted to <c>{DataPath}/keys</c>, which is therefore as
/// sensitive as the database itself and belongs in the same backup.
/// </remarks>
internal sealed class DataProtectionTokenProtector : ITokenProtector
{
    /// <summary>
    /// The purpose string binds ciphertext to this specific use. Changing it makes
    /// every previously stored token undecryptable, so it is versioned rather than
    /// edited: a future migration would introduce <c>.v2</c> and re-encrypt.
    /// </summary>
    private const string Purpose = "TheJukeBox.DriveRefreshToken.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<DataProtectionTokenProtector> _logger;

    public DataProtectionTokenProtector(
        IDataProtectionProvider provider,
        ILogger<DataProtectionTokenProtector> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _protector = provider.CreateProtector(Purpose);
        _logger = logger;
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        return _protector.Protect(plaintext);
    }

    public string? Unprotect(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (CryptographicException e)
        {
            // Covers both a lost/replaced key ring and a corrupted column. Logged once
            // per attempt at Warning: it is not a bug in the code, but it does mean a
            // user is about to be told to reconnect, and the operator should know the
            // reason is key material rather than Google revoking the grant.
            Log.TokenDecryptionFailed(_logger, e);
            return null;
        }
        catch (FormatException e)
        {
            // Unprotect base64-decodes first, so non-base64 input fails here rather
            // than as a CryptographicException.
            Log.TokenDecryptionFailed(_logger, e);
            return null;
        }
    }
}

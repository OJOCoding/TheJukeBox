namespace TheJukeBox.Infrastructure.Security;

/// <summary>
/// Encrypts and decrypts the Google refresh tokens held in the database.
/// </summary>
/// <remarks>
/// A refresh token plus the OAuth client secret is durable, unattended access to a
/// user's Drive: it does not expire on its own. Storing them in plaintext would mean
/// a leaked database file, or a stray backup, is a full compromise of every
/// connected account. Encrypting at rest narrows that to "database plus key ring".
/// </remarks>
public interface ITokenProtector
{
    /// <summary>Encrypts a refresh token for storage.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a stored refresh token, or returns <see langword="null"/> when the
    /// ciphertext cannot be read.
    /// </summary>
    /// <remarks>
    /// Returning null rather than throwing is deliberate. The realistic cause is a
    /// lost or replaced key ring, which affects every stored token at once; throwing
    /// would turn that into a 500 on every request, whereas null degrades to "this
    /// user must reconnect their Drive", which is both true and actionable.
    /// </remarks>
    string? Unprotect(string ciphertext);
}

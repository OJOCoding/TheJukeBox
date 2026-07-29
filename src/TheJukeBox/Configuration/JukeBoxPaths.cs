namespace TheJukeBox.Configuration;

/// <summary>
/// The resolved on-disk locations for this instance, computed once at startup.
/// </summary>
/// <remarks>
/// Injected rather than recomputed so no component is tempted to call
/// <see cref="Directory.GetCurrentDirectory"/> again. Everything here lives under a
/// single directory so "back up your JukeBox" is "copy one folder".
/// </remarks>
public sealed class JukeBoxPaths
{
    public required string DataPath { get; init; }

    /// <summary>Data Protection key ring. Losing this forces every user to re-consent.</summary>
    public string KeysPath => Path.Combine(DataPath, "keys");

    /// <summary>OAuth token storage. Superseded by the database in A4.</summary>
    public string TokensPath => Path.Combine(DataPath, "tokens");

    /// <summary>Disposable on-disk caches. Safe to delete; costs only warm-up time.</summary>
    public string CachePath => Path.Combine(DataPath, "cache");

    public string DatabaseFile => Path.Combine(DataPath, "jukebox.db");
}

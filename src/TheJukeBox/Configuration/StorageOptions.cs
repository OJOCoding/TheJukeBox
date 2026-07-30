namespace TheJukeBox.Configuration;

/// <summary>
/// Where this instance keeps its writable state.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Directory holding the SQLite database, Data Protection keys, and caches.
    /// Left unset it is resolved by <see cref="DataPathResolver"/>.
    /// </summary>
    public string? DataPath { get; init; }

    /// <summary>
    /// Apply pending EF Core migrations at startup. On by default, because a
    /// self-hoster pulling a new image expects it to work rather than to require a
    /// manual migration step.
    /// </summary>
    public bool AutoMigrate { get; init; } = true;

    /// <summary>
    /// How many pre-migration database backups to retain. Zero disables backups.
    /// </summary>
    /// <remarks>
    /// A failed upgrade on a self-hosted box otherwise has no recovery path at all,
    /// and the backup costs one <c>VACUUM INTO</c> of a database measured in
    /// megabytes.
    /// </remarks>
    public int BackupsToKeep { get; init; } = 5;
}

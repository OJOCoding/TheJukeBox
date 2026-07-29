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
}

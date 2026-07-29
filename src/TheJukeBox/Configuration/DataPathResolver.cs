namespace TheJukeBox.Configuration;

/// <summary>
/// Decides where this instance's writable state lives, once, at startup.
/// </summary>
/// <remarks>
/// Deliberately never uses <see cref="Directory.GetCurrentDirectory"/> (which the
/// old code did in three places, breaking whenever the app was launched from
/// anywhere but the project directory) and never the content root: writing into
/// the publish directory breaks read-only containers and is wiped on upgrade.
/// </remarks>
public static class DataPathResolver
{
    public const string EnvironmentVariable = "JUKEBOX_DATA_PATH";

    /// <summary>
    /// Resolves and creates the data directory. Precedence: explicit configuration,
    /// then <c>JUKEBOX_DATA_PATH</c>, then <c>/data</c> when it exists and is
    /// writable (the container convention), then a per-user application-data
    /// directory for desktop and development use.
    /// </summary>
    /// <exception cref="IOException">The resolved directory is not writable.</exception>
    public static string Resolve(StorageOptions options)
    {
        var path = FirstConfigured(options) ?? DefaultForEnvironment();

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);
        AssertWritable(full);

        return full;
    }

    private static string? FirstConfigured(StorageOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DataPath))
        {
            return options.DataPath;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;
    }

    private static string DefaultForEnvironment()
    {
        // A bind-mounted /data is the near-universal convention for self-hosted
        // containers. Only take it if it is genuinely usable, so a stray root-owned
        // /data on a dev box does not hijack the path.
        if (Directory.Exists("/data") && IsWritable("/data"))
        {
            return "/data";
        }

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        return Path.Combine(localAppData, "TheJukeBox");
    }

    private static void AssertWritable(string directory)
    {
        if (!IsWritable(directory))
        {
            throw new IOException(
                $"The data directory '{directory}' is not writable by this process. " +
                "In Docker this is almost always bind-mount ownership: the container " +
                "runs as a non-root user, so the host directory must be owned by it.");
        }
    }

    private static bool IsWritable(string directory)
    {
        // Probe by writing, rather than inspecting ACLs. Permission models differ
        // too much across Windows, Linux, and bind mounts for inspection to be
        // conclusive, and this runs exactly once per process start.
        var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");

        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

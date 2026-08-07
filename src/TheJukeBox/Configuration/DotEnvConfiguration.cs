using System.Diagnostics.CodeAnalysis;

namespace TheJukeBox.Configuration;

/// <summary>
/// Reads the repository's <c>.env</c> file during development.
/// </summary>
/// <remarks>
/// <para>
/// <c>.env</c> is a Docker Compose convention: Compose reads it and maps the names into
/// configuration keys (see <c>docker-compose.yml</c>). Nothing else reads it, so
/// <c>dotnet run</c> ignored it completely -- someone who filled in <c>.env</c> and then ran the
/// app locally got "NOT CONFIGURED" with their credentials sitting in a file the process never
/// opened. That is a genuinely confusing failure: the file exists, the values are right, and the
/// app says they are missing.
/// </para>
/// <para>
/// Rather than document the trap and leave it, this makes the same file work both ways. It is the
/// file the self-hosting guide tells people to edit, so it should be the file that configures the
/// app however the app is started.
/// </para>
/// <para>
/// <b>Development only.</b> A production deployment gets its configuration from the environment,
/// and silently reading a file from disk there would be a way for a stale checked-out <c>.env</c>
/// to override what an operator deliberately set.
/// </para>
/// <para>
/// <b>Lowest precedence.</b> Registered before every other source, so <c>appsettings</c>,
/// user-secrets, the launch profile and real environment variables all win. That ordering is what
/// lets the launch profile pin <c>App:PublicBaseUrl</c> to localhost while <c>.env</c> still
/// supplies the credentials -- the <c>.env</c> value is the public URL of a deployment, which is
/// exactly wrong for a local run.
/// </para>
/// </remarks>
internal static class DotEnvConfiguration
{
    /// <summary>
    /// How <c>.env</c> names map to configuration keys.
    /// </summary>
    /// <remarks>
    /// This table mirrors the <c>environment:</c> block of <c>docker-compose.yml</c> and has to
    /// stay in step with it, or the same file would configure the two ways of running differently.
    /// <c>DotEnvConfigurationTests</c> asserts every mapping here appears in that file.
    /// </remarks>
    private static readonly (string Variable, string Key)[] Mappings =
    [
        ("JUKEBOX_CLIENT_ID", "Authentication:Google:ClientId"),
        ("JUKEBOX_CLIENT_SECRET", "Authentication:Google:ClientSecret"),
        ("APP_PUBLIC_BASE_URL", "App:PublicBaseUrl"),
        ("APP_TRUST_PROXY", "App:TrustForwardedHeadersFromAnyProxy"),
        ("AUTH_REGISTRATION", "Auth:Registration"),
        ("METADATA_ONLINE", "Metadata:Online:Enabled"),
        ("METADATA_ONLINE_CONTACT", "Metadata:Online:ContactEmail"),
    ];

    /// <summary>A bound on the file read, so a stray huge file cannot be slurped into memory.</summary>
    private const int MaxBytes = 64 * 1024;

    /// <summary>
    /// Adds <c>.env</c> as the lowest-precedence source, if one is found.
    /// </summary>
    /// <returns>The path read, or null when there was nothing to read.</returns>
    public static string? AddRepositoryDotEnv(this IConfigurationBuilder builder, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var path = Locate(contentRootPath);

        if (path is null)
        {
            return null;
        }

        Dictionary<string, string?> values;

        try
        {
            values = Read(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable .env must not stop the app starting. Every other configuration source
            // still applies, and startup validation will say what is missing.
            return null;
        }

        if (values.Count == 0)
        {
            return null;
        }

        // Inserted at the front rather than appended: Add() would make this the *highest*
        // precedence source, and a checked-out .env would then override the launch profile.
        builder.Sources.Insert(0, new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
        {
            InitialData = values,
        });

        return path;
    }

    /// <summary>
    /// Finds <c>.env</c> by walking up from the content root.
    /// </summary>
    /// <remarks>
    /// The content root is <c>src/TheJukeBox</c> while the file lives at the repository root, so
    /// this cannot just look in one place. The walk stops at the repository marker so it can never
    /// wander out of the checkout and pick up an unrelated <c>.env</c> from a parent directory.
    /// </remarks>
    private static string? Locate(string contentRootPath)
    {
        for (var dir = new DirectoryInfo(contentRootPath); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".env");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            // The solution file marks the top of the checkout.
            if (File.Exists(Path.Combine(dir.FullName, "TheJukeBox.slnx")))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the recognised names out of a <c>.env</c> file.
    /// </summary>
    /// <remarks>
    /// Deliberately minimal: <c>KEY=VALUE</c>, one per line, <c>#</c> comments, and one optional
    /// layer of surrounding quotes. It does not implement variable interpolation or multi-line
    /// values, because Compose's own handling of those differs between versions and a value that
    /// two readers disagree about is worse than one this refuses to read. Unrecognised names are
    /// ignored rather than injected, so the file can hold things only Compose cares about.
    /// </remarks>
    private static Dictionary<string, string?> Read(string path)
    {
        var found = new Dictionary<string, string?>(StringComparer.Ordinal);

        var info = new FileInfo(path);

        if (info.Length > MaxBytes)
        {
            return found;
        }

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            // Compose permits a leading "export".
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0)
            {
                continue;
            }

            var name = line[..equals].Trim();

            if (!TryMap(name, out var key))
            {
                continue;
            }

            var value = Unquote(line[(equals + 1)..].Trim());

            // An empty value is treated as absent, so a commented-out-by-emptying line behaves the
            // way it looks: startup validation complains about a missing value rather than the app
            // starting with a blank client id.
            if (value.Length > 0)
            {
                found[key] = value;
            }
        }

        return found;
    }

    private static bool TryMap(string variable, [NotNullWhen(true)] out string? key)
    {
        foreach (var (name, mapped) in Mappings)
        {
            if (string.Equals(name, variable, StringComparison.Ordinal))
            {
                key = mapped;
                return true;
            }
        }

        key = null;
        return false;
    }

    /// <summary>Strips one layer of matching quotes, as Compose does.</summary>
    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value[1..^1]
            : value;

    /// <summary>The mappings, for the test that keeps them in step with docker-compose.yml.</summary>
    internal static IReadOnlyList<(string Variable, string Key)> KnownMappings => Mappings;
}

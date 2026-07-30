using System.Text;
using Microsoft.Extensions.Options;

namespace TheJukeBox.Configuration;

/// <summary>
/// Turns an options-validation failure at startup into an explanation a
/// self-hoster can act on, then exits.
/// </summary>
/// <remarks>
/// A "setup mode" was considered and rejected: it would mean an unauthenticated
/// page that accepts a client secret, doubling the auth surface. Crash-looping
/// with a legible log line is the established convention for self-hosted
/// containers, and pairs correctly with <c>restart: unless-stopped</c>.
/// </remarks>
public static class StartupValidation
{
    /// <summary>Exit code 78 is EX_CONFIG from sysexits.h: a configuration error.</summary>
    public const int ConfigurationErrorExitCode = 78;

    private static readonly Dictionary<string, string> SectionByOptionsTypeName = new(StringComparer.Ordinal)
    {
        [nameof(GoogleOAuthOptions)] = GoogleOAuthOptions.SectionName,
        [nameof(AppOptions)] = AppOptions.SectionName,
        [nameof(StorageOptions)] = StorageOptions.SectionName,
    };

    /// <summary>
    /// Writes a boxed diagnostic naming every missing key in both configuration and
    /// environment-variable form, then terminates the process. Never returns.
    /// </summary>
    public static void PrintAndExit(Exception exception)
    {
        var report = new StringBuilder();
        const string rule = "================================================================================";

        report.AppendLine();
        report.AppendLine(rule);
        report.AppendLine(" THE JUKEBOX -- CANNOT START: CONFIGURATION REQUIRED");
        report.AppendLine(rule);
        report.AppendLine();

        var failures = Flatten(exception).ToArray();

        if (failures.Length == 0)
        {
            report.AppendLine($" {exception.Message}");
        }
        else
        {
            report.AppendLine(" The following settings are missing or invalid:");
            report.AppendLine();

            foreach (var failure in failures)
            {
                var section = SectionByOptionsTypeName.GetValueOrDefault(failure.OptionsTypeName);

                foreach (var message in failure.Messages)
                {
                    report.AppendLine($"   * {message}");

                    if (section is not null)
                    {
                        var key = $"{section}:{PropertyNameFrom(message)}";
                        report.AppendLine($"       config key : {key}");
                        report.AppendLine($"       env var    : {key.Replace(":", "__", StringComparison.Ordinal)}");
                    }

                    report.AppendLine();
                }
            }
        }

        report.AppendLine(" Set them for local development:");
        report.AppendLine();
        report.AppendLine("   dotnet user-secrets set \"Authentication:Google:ClientId\" \"<your-client-id>\" \\");
        report.AppendLine("     --project src/TheJukeBox");
        report.AppendLine("   dotnet user-secrets set \"Authentication:Google:ClientSecret\" \"<your-secret>\" \\");
        report.AppendLine("     --project src/TheJukeBox");
        report.AppendLine();
        report.AppendLine(" Or in Docker, as environment variables (note the double underscores):");
        report.AppendLine();
        report.AppendLine("   Authentication__Google__ClientId=...");
        report.AppendLine("   Authentication__Google__ClientSecret=...");
        report.AppendLine("   App__PublicBaseUrl=https://music.example.com");
        report.AppendLine();
        report.AppendLine(" Obtain credentials at https://console.cloud.google.com/apis/credentials");
        report.AppendLine(" and enable the Google Drive API for the project.");
        report.AppendLine();
        report.AppendLine(" IMPORTANT: set the OAuth consent screen's publishing status to");
        report.AppendLine(" \"In production\". Left in \"Testing\", Google expires refresh tokens after");
        report.AppendLine(" 7 days and background library sync will silently stop working every week.");
        report.AppendLine(" Publishing without completing verification is fine for a personal");
        report.AppendLine(" instance; users see an \"unverified app\" warning and there is a 100-user cap.");
        report.AppendLine();
        report.AppendLine(" See docs/SELF-HOSTING.md for the full walkthrough.");
        report.AppendLine(rule);
        report.AppendLine();

        Console.Error.Write(report.ToString());
        Console.Error.Flush();

        Environment.Exit(ConfigurationErrorExitCode);
    }

    /// <summary>
    /// Reports an unusable database and terminates. Never returns.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="PrintAndExit"/> because the remedies are completely
    /// different: a storage failure is almost never about OAuth credentials, and
    /// printing the Google setup walkthrough would send the operator in the wrong
    /// direction.
    /// </remarks>
    public static void PrintStorageFailureAndExit(Exception exception, string dataPath)
    {
        ArgumentNullException.ThrowIfNull(exception);

        const string rule = "================================================================================";

        var report = new StringBuilder();

        report.AppendLine();
        report.AppendLine(rule);
        report.AppendLine(" THE JUKEBOX -- CANNOT START: DATABASE UNAVAILABLE");
        report.AppendLine(rule);
        report.AppendLine();
        report.AppendLine($" Data directory : {dataPath}");
        report.AppendLine($" Reason         : {exception.Message}");
        report.AppendLine();
        report.AppendLine(" Most common causes, in order:");
        report.AppendLine();
        report.AppendLine("   1. Bind-mount ownership. The container runs as a non-root user, so a");
        report.AppendLine("      host directory mounted at /data must be writable by it.");
        report.AppendLine("      Try:  sudo chown -R 64198:64198 ./data");
        report.AppendLine("   2. The volume is mounted read-only.");
        report.AppendLine("   3. The disk is full.");
        report.AppendLine("   4. A failed earlier upgrade left the schema half-applied. Restore the");
        report.AppendLine($"      newest file from {Path.Combine(dataPath, "backups")} over");
        report.AppendLine("      jukebox.db and retry.");
        report.AppendLine();
        report.AppendLine(" Set Storage:DataPath (or JUKEBOX_DATA_PATH) to move the data directory.");
        report.AppendLine(" Set Storage:AutoMigrate=false to start without applying migrations.");
        report.AppendLine();
        report.AppendLine(" See docs/SELF-HOSTING.md for the full walkthrough.");
        report.AppendLine(rule);
        report.AppendLine();

        Console.Error.Write(report.ToString());
        Console.Error.Flush();

        Environment.Exit(ConfigurationErrorExitCode);
    }

    /// <summary>
    /// True when this exception (or anything it wraps) is an options-validation
    /// failure, which is the only kind this class is equipped to explain.
    /// </summary>
    public static bool IsConfigurationFailure(Exception exception) =>
        Unwrap(exception).Any(e => e is OptionsValidationException);

    private static IEnumerable<(string OptionsTypeName, IEnumerable<string> Messages)> Flatten(Exception exception) =>
        Unwrap(exception)
            .OfType<OptionsValidationException>()
            .Select(e => (e.OptionsType.Name, e.Failures));

    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        // The host's startup validator may surface a single OptionsValidationException
        // or an AggregateException wrapping several, depending on how many option
        // types failed. Handle both rather than guessing.
        switch (exception)
        {
            case AggregateException aggregate:
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    foreach (var e in Unwrap(inner))
                    {
                        yield return e;
                    }
                }

                break;

            default:
                yield return exception;

                if (exception.InnerException is not null)
                {
                    foreach (var e in Unwrap(exception.InnerException))
                    {
                        yield return e;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Recovers the offending property name from an options-validation message.
    /// </summary>
    /// <remarks>
    /// The options infrastructure formats DataAnnotations failures as
    /// <c>DataAnnotation validation failed for 'TOptions' members: 'PropertyName'
    /// with the error: '...'</c>. Reading the <c>members:</c> clause is exact;
    /// scanning for the first capitalised word is not, and yielded
    /// "DataAnnotation" -- which would have sent a self-hoster looking for a config
    /// key that does not exist.
    /// </remarks>
    private static string PropertyNameFrom(string validationMessage)
    {
        const string marker = "members: '";

        var start = validationMessage.IndexOf(marker, StringComparison.Ordinal);

        if (start >= 0)
        {
            start += marker.Length;
            var end = validationMessage.IndexOf('\'', start);

            if (end > start)
            {
                // A single failure can name several members: "members: 'A,B'".
                return validationMessage[start..end].Replace(",", ", ", StringComparison.Ordinal);
            }
        }

        // Custom .Validate(...) messages carry no members clause. Fall back to the
        // first quoted token, then to a placeholder.
        var quoteStart = validationMessage.IndexOf('\'', StringComparison.Ordinal);

        if (quoteStart >= 0)
        {
            var quoteEnd = validationMessage.IndexOf('\'', quoteStart + 1);

            if (quoteEnd > quoteStart + 1)
            {
                return validationMessage[(quoteStart + 1)..quoteEnd];
            }
        }

        return "<key>";
    }
}

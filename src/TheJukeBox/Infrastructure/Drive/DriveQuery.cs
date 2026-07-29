using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using TheJukeBox.Errors;

namespace TheJukeBox.Infrastructure.Drive;

/// <summary>
/// Builds Google Drive <c>q</c> search expressions without letting caller input
/// escape its string literal.
/// </summary>
/// <remarks>
/// The previous code interpolated user input straight into the query, e.g.
/// <c>request.Q = $"name contains '{fileName}' and trashed=false"</c>, in five
/// places. A single apostrophe in a track name -- extremely common in music
/// libraries ("Don't Stop") -- terminated the literal and produced an API error;
/// a crafted value could rewrite the predicate entirely.
/// <para>
/// Drive's grammar quotes string values with <c>'</c> and escapes with <c>\</c>,
/// so the backslash must be doubled <em>before</em> the apostrophe is escaped.
/// Doing it the other way round turns <c>\</c> + <c>'</c> into <c>\\'</c>, which
/// escapes the backslash and leaves the apostrophe live.
/// </para>
/// </remarks>
public static partial class DriveQuery
{
    /// <summary>
    /// Longest accepted free-text term. Drive rejects very long queries anyway, and
    /// a bound keeps a pathological input from being built into a megabyte string.
    /// </summary>
    public const int MaxTermLength = 512;

    /// <summary>
    /// Drive file and folder identifiers are URL-safe base64-ish tokens. Because the
    /// alphabet excludes both the quote and the backslash, identifiers are validated
    /// rather than escaped: anything not matching this shape is not an id at all.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-]{10,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileIdPattern { get; }

    public static bool IsValidFileId([NotNullWhen(true)] string? id) =>
        !string.IsNullOrEmpty(id) && FileIdPattern.IsMatch(id);

    /// <summary>Validates a Drive identifier for use in a query or request path.</summary>
    /// <exception cref="InvalidDriveIdentifierException">The id is missing or malformed.</exception>
    public static string RequireFileId(string? id) =>
        IsValidFileId(id)
            ? id
            : throw new InvalidDriveIdentifierException(
                "A Drive identifier may contain only letters, digits, '-' and '_'.");

    /// <summary>
    /// Escapes a value for embedding between single quotes in a Drive query, and
    /// strips control characters (which Drive rejects and which can smuggle newlines
    /// into logs).
    /// </summary>
    public static string EscapeValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var trimmed = value.Length > MaxTermLength ? value[..MaxTermLength] : value;
        var escaped = new StringBuilder(trimmed.Length + 8);

        foreach (var c in trimmed)
        {
            switch (c)
            {
                case '\\':
                    escaped.Append(@"\\");
                    break;
                case '\'':
                    escaped.Append(@"\'");
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        escaped.Append(c);
                    }

                    break;
            }
        }

        return escaped.ToString();
    }

    /// <summary>Only audio files, excluding trashed ones.</summary>
    public static string AllAudio() => "mimeType contains 'audio/' and trashed = false";

    /// <summary>Audio files whose name contains <paramref name="term"/>.</summary>
    public static string AudioNamed(string? term) =>
        string.IsNullOrWhiteSpace(term)
            ? AllAudio()
            : $"name contains '{EscapeValue(term)}' and {AllAudio()}";

    /// <summary>Audio files directly inside a folder.</summary>
    /// <exception cref="InvalidDriveIdentifierException">The folder id is malformed.</exception>
    public static string AudioInFolder(string? folderId) =>
        $"'{RequireFileId(folderId)}' in parents and {AllAudio()}";

    /// <summary>Any non-trashed file whose name contains <paramref name="term"/>.</summary>
    public static string Named(string? term) =>
        string.IsNullOrWhiteSpace(term)
            ? "trashed = false"
            : $"name contains '{EscapeValue(term)}' and trashed = false";
}

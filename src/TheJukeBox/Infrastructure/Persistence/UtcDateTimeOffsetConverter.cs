using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TheJukeBox.Infrastructure.Persistence;

/// <summary>
/// Stores every <see cref="DateTimeOffset"/> as a fixed-width UTC ISO-8601 string.
/// </summary>
/// <remarks>
/// <para>
/// SQLite has no date type, and EF Core's SQLite provider refuses outright to
/// translate <c>ORDER BY</c> over a <see cref="DateTimeOffset"/> expression:
/// "SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses."
/// Converting to <see cref="DateTimeOffset"/> does not help, because the provider type
/// is unchanged. The value has to reach the database as something SQLite can order,
/// which means TEXT or a number.
/// </para>
/// <para>
/// TEXT is chosen over a numeric tick count so the tables stay readable to anyone
/// opening them with <c>sqlite3</c> to diagnose a stuck sync, which for a self-hosted
/// app is the realistic support channel. That is the same reasoning applied to
/// <see cref="Domain.SyncStatus"/>.
/// </para>
/// <para>
/// Two properties of the format matter, and both are load-bearing:
/// </para>
/// <list type="number">
/// <item>
/// <b>Always UTC.</b> SQLite compares TEXT byte by byte, so a column holding mixed
/// offsets would sort by local wall clock. Two rows written either side of a
/// daylight-saving change would order wrongly, and that surfaces as a mis-ordered
/// play history rather than as an error.
/// </item>
/// <item>
/// <b>Fixed width, with the fraction never trimmed.</b> Lexicographic ordering only
/// equals chronological ordering when every value has the same length. Formats that
/// drop trailing zeros (<c>FFFFFFF</c>) make <c>...:05Z</c> sort after
/// <c>...:05.9Z</c>, because <c>'Z'</c> (0x5A) sorts after <c>'.'</c> (0x2E).
/// </item>
/// </list>
/// <para>
/// Round-tripping is exact to the tick, which is the full precision
/// <see cref="DateTimeOffset"/> carries.
/// </para>
/// </remarks>
internal sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, string>
{
    /// <summary>Fixed 28 characters: "2026-07-30T07:34:00.0000000Z".</summary>
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>Column length, so the schema documents the invariant.</summary>
    public const int StoredLength = 28;

    public UtcDateTimeOffsetConverter()
        : base(
            value => value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture),
            value => Parse(value))
    {
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.ParseExact(
            value,
            Format,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}

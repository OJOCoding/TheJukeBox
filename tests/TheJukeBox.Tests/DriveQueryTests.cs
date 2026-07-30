using Microsoft.AspNetCore.Http;
using TheJukeBox.Errors;
using TheJukeBox.Infrastructure.Drive;

namespace TheJukeBox.Tests;

public class DriveQueryEscapingTests
{
    [Theory]
    [InlineData("Blue in Green", "Blue in Green")]
    [InlineData("", "")]
    // An apostrophe is the common case, not an attack: music libraries are full of
    // them, and each one used to terminate the query literal and fail the request.
    [InlineData("Don't Stop", @"Don\'t Stop")]
    [InlineData("AC\\DC", @"AC\\DC")]
    // Ordering proof. The backslash must be doubled first; escaping the apostrophe
    // first would leave the backslash to escape the escape and free the quote.
    [InlineData(@"\'", @"\\\'")]
    [InlineData(@"a\'b", @"a\\\'b")]
    [InlineData(@"''", @"\'\'")]
    [InlineData(@"\\", @"\\\\")]
    public void Escapes_backslashes_before_quotes(string input, string expected) =>
        Assert.Equal(expected, DriveQuery.EscapeValue(input));

    [Theory]
    [InlineData("line\nbreak", "linebreak")]
    [InlineData("tab\there", "tabhere")]
    [InlineData("null\0byte", "nullbyte")]
    [InlineData("carriage\rreturn", "carriagereturn")]
    public void Strips_control_characters(string input, string expected) =>
        Assert.Equal(expected, DriveQuery.EscapeValue(input));

    [Fact]
    public void Caps_absurdly_long_terms() =>
        Assert.Equal(DriveQuery.MaxTermLength, DriveQuery.EscapeValue(new string('a', 10_000)).Length);

    [Fact]
    public void Null_escapes_to_empty() =>
        Assert.Equal(string.Empty, DriveQuery.EscapeValue(null));

    /// <summary>
    /// The property that actually matters: whatever goes in, the escaped result
    /// cannot close the surrounding single-quoted literal. Asserting on exact
    /// output only proves the cases someone thought to enumerate; this proves the
    /// invariant across every adversarial input below.
    /// </summary>
    [Theory]
    [InlineData("' or '1'='1")]
    [InlineData("' or name contains '")]
    [InlineData("x' and trashed = true and 'y")]
    [InlineData(@"\' or '1'='1")]
    [InlineData(@"\\' or '1'='1")]
    [InlineData(@"\\\' or '1'='1")]
    [InlineData("'")]
    [InlineData(@"\")]
    [InlineData(@"''''''")]
    [InlineData(@"\\\\\\")]
    [InlineData(@"'\'\'\")]
    [InlineData("mimeType='application/vnd.google-apps.folder'")]
    public void Escaped_values_cannot_terminate_the_literal(string hostile)
    {
        var escaped = DriveQuery.EscapeValue(hostile);

        Assert.False(
            ContainsUnescapedQuote(escaped),
            $"'{hostile}' escaped to '{escaped}', which breaks out of the literal.");
    }

    [Theory]
    [InlineData("' or '1'='1")]
    [InlineData(@"\' or '1'='1")]
    [InlineData("Don't Stop")]
    public void Built_queries_keep_their_structure(string hostile)
    {
        var query = DriveQuery.AudioNamed(hostile);

        // Exactly two quoted literals survive: the caller's term and 'audio/'.
        Assert.Equal(2, CountLiterals(query));
        Assert.EndsWith("trashed = false", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks the escaped text the way Drive's parser would: a backslash consumes
    /// the character after it, so any apostrophe reached directly is unescaped and
    /// would end the literal.
    /// </summary>
    private static bool ContainsUnescapedQuote(string escaped)
    {
        for (var i = 0; i < escaped.Length; i++)
        {
            if (escaped[i] == '\\')
            {
                i++;
                continue;
            }

            if (escaped[i] == '\'')
            {
                return true;
            }
        }

        return false;
    }

    private static int CountLiterals(string query)
    {
        var literals = 0;

        for (var i = 0; i < query.Length; i++)
        {
            if (query[i] == '\\')
            {
                i++;
                continue;
            }

            if (query[i] == '\'')
            {
                literals++;
            }
        }

        return literals / 2;
    }
}

public class DriveQueryIdentifierTests
{
    [Theory]
    [InlineData("1BxiMVs0XRA5nFMdKvBdBZjgmUUqptlbs74OgvE2upms")]
    [InlineData("0B7_pZC-o8Xz-fjRhVGdVU2xBaWM")]
    [InlineData("abcdefghij")]
    [InlineData("a-b_c-d_e-f")]
    public void Accepts_realistic_identifiers(string id) =>
        Assert.True(DriveQuery.IsValidFileId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tooshort")]
    [InlineData("has spaces in it")]
    [InlineData("has'quote'inside")]
    [InlineData(@"has\backslash")]
    [InlineData("' in parents or '1'='1")]
    [InlineData("../../etc/passwd")]
    [InlineData("id/with/slashes")]
    [InlineData("id.with.dots")]
    public void Rejects_anything_else(string? id) =>
        Assert.False(DriveQuery.IsValidFileId(id));

    [Fact]
    public void Rejects_identifiers_beyond_the_length_bound() =>
        Assert.False(DriveQuery.IsValidFileId(new string('a', 257)));

    [Fact]
    public void RequireFileId_returns_valid_input() =>
        Assert.Equal("abcdefghij", DriveQuery.RequireFileId("abcdefghij"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("' in parents or '1'='1")]
    public void RequireFileId_throws_a_typed_400(string? id)
    {
        var thrown = Assert.Throws<InvalidDriveIdentifierException>(() => DriveQuery.RequireFileId(id));

        Assert.Equal(StatusCodes.Status400BadRequest, thrown.StatusCode);
        Assert.Equal(ProblemTypes.InvalidDriveIdentifier, thrown.ProblemType);
    }

    [Fact]
    public void Folder_queries_validate_rather_than_escape() =>
        Assert.Throws<InvalidDriveIdentifierException>(
            () => DriveQuery.AudioInFolder("' in parents or '1'='1"));

    [Fact]
    public void Folder_queries_embed_a_validated_id()
    {
        const string folderId = "1BxiMVs0XRA5nFMdKvBdBZjgmUUqptlbs";

        Assert.Equal(
            $"'{folderId}' in parents and mimeType contains 'audio/' and trashed = false",
            DriveQuery.AudioInFolder(folderId));
    }
}

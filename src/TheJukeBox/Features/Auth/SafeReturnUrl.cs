using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace TheJukeBox.Features.Auth;

/// <summary>
/// Guards the "send the user back where they came from" behaviour.
/// </summary>
/// <remarks>
/// A <c>returnUrl</c> taken from a request and used in a redirect without validation is
/// an open redirect: an attacker sends <c>/api/auth/login?returnUrl=https://evil.example</c>,
/// the victim sees a genuine sign-in on the real domain, and is then handed to the
/// attacker's page still believing they are in the application.
/// </remarks>
public static class SafeReturnUrl
{
    public const string Default = "/";

    /// <summary>
    /// Reduces a caller-supplied return target to a safe same-origin path, or to
    /// <see cref="Default"/>.
    /// </summary>
    public static string Sanitise(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Default;
        }

        // Must be a rooted path. Anything absolute carries a scheme and host, which by
        // definition is not this origin.
        if (candidate[0] != '/')
        {
            return Default;
        }

        // "//evil.example" is protocol-relative: browsers treat it as absolute, and it
        // is the case a naive "starts with /" check lets through. "/\evil.example" is
        // the same trick with a backslash, which some browsers normalise to a slash.
        if (candidate.Length > 1 && (candidate[1] == '/' || candidate[1] == '\\'))
        {
            return Default;
        }

        // Control characters can split headers on a careless downstream writer.
        if (candidate.Any(char.IsControl))
        {
            return Default;
        }

        return candidate;
    }
}

/// <summary>
/// Rewrites a single query-string parameter on an absolute URL.
/// </summary>
internal static class QueryStringRewriter
{
    /// <summary>
    /// Returns <paramref name="url"/> with <paramref name="name"/> set to
    /// <paramref name="value"/>, preserving every other parameter and its order.
    /// </summary>
    public static string Replace(string url, string name, string value)
    {
        var separator = url.IndexOf('?', StringComparison.Ordinal);

        if (separator < 0)
        {
            return QueryHelpers.AddQueryString(url, name, value);
        }

        var withoutQuery = url[..separator];
        var parsed = QueryHelpers.ParseQuery(url[separator..]);

        parsed[name] = new StringValues(value);

        // Rebuilt through AddQueryString so every value is escaped consistently; hand
        // concatenation here is how an unencoded scope list or state token corrupts the
        // authorisation request.
        var rebuilt = parsed.SelectMany(
            pair => pair.Value.Select(single => new KeyValuePair<string, string?>(pair.Key, single)));

        return QueryHelpers.AddQueryString(withoutQuery, rebuilt);
    }
}

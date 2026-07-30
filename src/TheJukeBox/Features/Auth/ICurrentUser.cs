namespace TheJukeBox.Features.Auth;

/// <summary>
/// The user the current request is acting as.
/// </summary>
/// <remarks>
/// Exists so that services needing "whose Drive is this?" declare that dependency
/// instead of reaching into <c>IHttpContextAccessor</c> and reading claims by string
/// key in several places. One implementation means one place where the answer can be
/// wrong.
/// </remarks>
public interface ICurrentUser
{
    /// <summary>The signed-in user's id, or null when the request is anonymous.</summary>
    Guid? Id { get; }

    /// <summary>
    /// The signed-in user's id, or throws.
    /// </summary>
    /// <remarks>
    /// Every caller sits behind <c>RequireAuthorization()</c>, so a throw here means an
    /// endpoint was registered without it. Failing loudly is right: the alternative is
    /// silently querying with <c>Guid.Empty</c> and returning an empty library, which
    /// looks like data loss rather than a missing attribute.
    /// </remarks>
    Guid RequireId();
}

internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? Id => accessor.HttpContext?.User.UserId();

    public Guid RequireId() =>
        Id ?? throw new InvalidOperationException(
            "No authenticated user on the current request. The endpoint is missing RequireAuthorization().");
}

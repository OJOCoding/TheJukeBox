using Google.Apis.Drive.v3;

namespace TheJukeBox.Infrastructure.Drive;

/// <summary>
/// Builds a Google Drive client bound to one specific user's grant.
/// </summary>
/// <remarks>
/// Every Drive call in the application goes through here, and the user id is not
/// optional. That is the structural half of the authorization fix: it is not possible
/// to obtain a Drive client without saying whose Drive it is, so "accidentally used
/// the wrong account's token" is not a mistake this codebase can express.
/// </remarks>
public interface IDriveClientFactory
{
    /// <summary>
    /// A Drive client acting as <paramref name="userId"/>.
    /// </summary>
    /// <exception cref="Errors.DriveConsentRequiredException">
    /// The user has no usable grant, so the UI must prompt them to reconnect.
    /// </exception>
    Task<DriveService> CreateAsync(Guid userId, CancellationToken cancellationToken = default);
}

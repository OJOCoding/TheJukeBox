namespace TheJukeBox.Errors;

/// <summary>
/// An exception that already knows how it should be rendered to a client.
/// </summary>
/// <remarks>
/// Existing code caught everything per-action and returned
/// <c>BadRequest(ex.Message)</c>, which leaked internal exception text and made
/// authentication failures indistinguishable from genuine bad input. Carrying the
/// status, type, and disclosure decision on the exception itself lets one handler
/// do the right thing without any per-action plumbing.
/// </remarks>
public abstract class JukeBoxException : Exception
{
    protected JukeBoxException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public abstract int StatusCode { get; }

    public abstract string ProblemType { get; }

    public abstract string Title { get; }

    /// <summary>
    /// Whether <see cref="Exception.Message"/> may be sent to the client. False by
    /// default for anything derived from an upstream failure, so stack-adjacent
    /// detail cannot escape by accident.
    /// </summary>
    public virtual bool MessageIsSafeToExpose => true;
}

/// <summary>The caller has no usable Google Drive grant on this instance.</summary>
public sealed class DriveConsentRequiredException : JukeBoxException
{
    public DriveConsentRequiredException(string? message = null)
        : base(message ?? "Google Drive access has not been granted, or the grant is no longer valid.")
    {
    }

    public override int StatusCode => StatusCodes.Status403Forbidden;

    public override string ProblemType => ProblemTypes.DriveConsentRequired;

    public override string Title => "Google Drive access is required.";
}

/// <summary>A requested resource does not exist, or is not visible to this caller.</summary>
/// <remarks>
/// Cross-owner access must raise this rather than a 403: confirming that an id
/// exists but belongs to someone else is itself a disclosure.
/// </remarks>
public sealed class ResourceNotFoundException : JukeBoxException
{
    public ResourceNotFoundException(string message = "The requested resource was not found.")
        : base(message)
    {
    }

    public override int StatusCode => StatusCodes.Status404NotFound;

    public override string ProblemType => ProblemTypes.NotFound;

    public override string Title => "Not found.";
}

/// <summary>A supplied Drive file or folder identifier was malformed.</summary>
public sealed class InvalidDriveIdentifierException : JukeBoxException
{
    public InvalidDriveIdentifierException(string message = "The supplied Drive identifier is not valid.")
        : base(message)
    {
    }

    public override int StatusCode => StatusCodes.Status400BadRequest;

    public override string ProblemType => ProblemTypes.InvalidDriveIdentifier;

    public override string Title => "Invalid Drive identifier.";
}

/// <summary>An upstream Google Drive call failed.</summary>
public sealed class DriveUnavailableException : JukeBoxException
{
    public DriveUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public override int StatusCode => StatusCodes.Status502BadGateway;

    public override string ProblemType => ProblemTypes.DriveUnavailable;

    public override string Title => "Google Drive is currently unavailable.";

    /// <summary>Upstream messages can carry request detail; keep them server-side.</summary>
    public override bool MessageIsSafeToExpose => false;
}

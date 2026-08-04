using TheJukeBox.Configuration;
using TheJukeBox.Domain;

namespace TheJukeBox.Diagnostics;

/// <summary>
/// Source-generated log messages.
/// </summary>
/// <remarks>
/// <c>[LoggerMessage]</c> emits a strongly typed, cached delegate per message, so
/// there is no boxing of arguments and no format parsing on the hot path. This is
/// what CA1848 asks for, and it matters here because the request pipeline logs on
/// every failure. Event ids are stable so log-based alerting can key on them.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Debug,
        Message = "Request to {Path} was aborted by the client.")]
    public static partial void RequestAborted(ILogger logger, string path);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "Unhandled exception for {Method} {Path}.")]
    public static partial void Unhandled(ILogger logger, Exception exception, string method, string path);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "{ProblemType} for {Method} {Path}: {Reason}")]
    public static partial void ClientProblem(
        ILogger logger,
        string? problemType,
        string method,
        string path,
        string reason);

    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Information,
        Message = "Data directory resolved to {DataPath}.")]
    public static partial void DataPathResolved(ILogger logger, string dataPath);

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Information,
        Message = "OAuth redirect URIs for this instance (register these verbatim in Google Cloud Console): "
                  + "sign-in {SignInRedirectUri}, Drive {DriveRedirectUri}")]
    public static partial void RedirectUris(
        ILogger logger,
        string signInRedirectUri,
        string driveRedirectUri);

    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Warning,
        Message = "The configured Google OAuth client id ({ClientIdHint}) does not look like a "
                  + "real one, so sign-in will fail with Google's \"invalid_client\" error. Get a "
                  + "Web application OAuth client from "
                  + "https://console.cloud.google.com/apis/credentials, then supply it the way "
                  + "that matches how you run this:\n"
                  + "  Docker:      cp .env.example .env and fill in JUKEBOX_CLIENT_ID and "
                  + "JUKEBOX_CLIENT_SECRET, then docker compose up -d\n"
                  + "  dotnet run:  cd src/TheJukeBox && dotnet user-secrets set "
                  + "\"Authentication:Google:ClientId\" \"<id>\" (and ClientSecret)\n"
                  + "Note that .env is read by docker compose only. It has no effect on "
                  + "dotnet run. See docs/SELF-HOSTING.md.")]
    public static partial void GoogleCredentialsLookLikePlaceholders(ILogger logger, string clientIdHint);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Warning,
        Message = "Failed to load the stored Drive token; treating the caller as unauthenticated.")]
    public static partial void TokenLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1021,
        Level = LogLevel.Warning,
        Message = "The Google OAuth callback failed.")]
    public static partial void OAuthCallbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1022,
        Level = LogLevel.Warning,
        Message = "A stored Drive refresh token could not be decrypted. The Data Protection key "
                  + "ring under {{DataPath}}/keys has most likely been lost or replaced; affected "
                  + "users must reconnect their Drive.")]
    public static partial void TokenDecryptionFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1023,
        Level = LogLevel.Information,
        Message = "Signed in {UserId} (Google subject {GoogleSubject}); new account: {IsNewAccount}.")]
    public static partial void SignedIn(
        ILogger logger,
        Guid userId,
        string googleSubject,
        bool isNewAccount);

    [LoggerMessage(
        EventId = 1024,
        Level = LogLevel.Warning,
        Message = "Google returned no refresh token for {UserId}. Background sync cannot run until "
                  + "the user reconnects with prompt=consent.")]
    public static partial void NoRefreshTokenIssued(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 1025,
        Level = LogLevel.Warning,
        Message = "Retired the Drive grant for {UserId}: {ErrorCode}. The user must reconnect.")]
    public static partial void DriveGrantRetired(ILogger logger, Guid userId, string errorCode);

    [LoggerMessage(
        EventId = 1026,
        Level = LogLevel.Information,
        Message = "The first account on this instance ({UserId}) has been made an administrator.")]
    public static partial void FirstUserPromoted(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 1027,
        Level = LogLevel.Information,
        Message = "Rejected the session cookie for {UserId}: {Reason}. The user has been signed out.")]
    public static partial void SessionRejected(ILogger logger, Guid userId, string reason);

    [LoggerMessage(
        EventId = 1028,
        Level = LogLevel.Information,
        Message = "New account {UserId} ({Email}) registered as {Status} under registration mode "
                  + "{RegistrationMode}.")]
    public static partial void AccountRegistered(
        ILogger logger,
        Guid userId,
        string? email,
        UserStatus status,
        RegistrationMode registrationMode);

    [LoggerMessage(
        EventId = 1029,
        Level = LogLevel.Information,
        Message = "Administrator {ActorId} changed account {UserId} to {Status}.")]
    public static partial void AccountStatusChanged(
        ILogger logger,
        Guid actorId,
        Guid userId,
        UserStatus status);

    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Debug,
        Message = "Drive listing returned {Count} audio file(s).")]
    public static partial void DriveListed(ILogger logger, int count);

    [LoggerMessage(
        EventId = 1031,
        Level = LogLevel.Information,
        Message = "Enumerated {Count} audio file(s) from Drive across {Pages} page(s).")]
    public static partial void DriveEnumerated(ILogger logger, int count, int pages);

    [LoggerMessage(
        EventId = 1032,
        Level = LogLevel.Information,
        Message = "Sync for {UserId} finished: {Added} added, {Updated} updated, {Removed} removed, "
                  + "{Unchanged} unchanged.")]
    public static partial void SyncCompleted(
        ILogger logger,
        Guid userId,
        int added,
        int updated,
        int removed,
        int unchanged);

    [LoggerMessage(
        EventId = 1033,
        Level = LogLevel.Warning,
        Message = "Sync for {UserId} failed.")]
    public static partial void SyncFailed(ILogger logger, Guid userId, Exception exception);

    [LoggerMessage(
        EventId = 1034,
        Level = LogLevel.Debug,
        Message = "Sync for {UserId} was already queued or running; the request was folded into it.")]
    public static partial void SyncAlreadyPending(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 1035,
        Level = LogLevel.Information,
        Message = "Incremental sync for {UserId}: {Count} change(s) over {Pages} page(s).")]
    public static partial void ChangesApplied(ILogger logger, Guid userId, int count, int pages);

    [LoggerMessage(
        EventId = 1036,
        Level = LogLevel.Debug,
        Message = "Fetched {Count} change(s) for {UserId}; final page: {IsFinalPage}.")]
    public static partial void ChangesFetched(ILogger logger, Guid userId, int count, bool isFinalPage);

    [LoggerMessage(
        EventId = 1037,
        Level = LogLevel.Warning,
        Message = "Drive rejected the stored change token for {UserId}. Falling back to a full "
                  + "listing and starting a fresh token; without this, automatic sync would stop "
                  + "permanently with no visible cause.")]
    public static partial void ChangeTokenExpired(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 1038,
        Level = LogLevel.Debug,
        Message = "Scheduled sync for {Count} user(s) due for a refresh.")]
    public static partial void ScheduledSyncQueued(ILogger logger, int count);

    [LoggerMessage(
        EventId = 1039,
        Level = LogLevel.Debug,
        Message = "Skipping {UserId}: {Failures} consecutive failure(s), backing off for "
                  + "another {BackoffMinutes} minute(s).")]
    public static partial void SyncBackingOff(
        ILogger logger,
        Guid userId,
        int failures,
        double backoffMinutes);

    [LoggerMessage(
        EventId = 1050,
        Level = LogLevel.Information,
        Message = "Metadata pass starting for {UserId}: {Candidates} track(s) need tags read.")]
    public static partial void MetadataPassStarted(ILogger logger, Guid userId, int candidates);

    [LoggerMessage(
        EventId = 1051,
        Level = LogLevel.Information,
        Message = "Metadata pass for {UserId} finished: {Examined} examined, {Tagged} from tags, "
                  + "{Guessed} from file names, {ArtStored} cover image(s) stored, "
                  + "{RangeRequests} range request(s).")]
    public static partial void MetadataPassCompleted(
        ILogger logger,
        Guid userId,
        int examined,
        int tagged,
        int guessed,
        int artStored,
        int rangeRequests);

    [LoggerMessage(
        EventId = 1052,
        Level = LogLevel.Debug,
        Message = "No tag format recognised in {FileName} ({Hint}); falling back to the file name.")]
    public static partial void TagFormatUnrecognised(ILogger logger, string fileName, string hint);

    [LoggerMessage(
        EventId = 1053,
        Level = LogLevel.Warning,
        Message = "Reading tags from {FileName} failed; falling back to the file name.")]
    public static partial void TagReadFailed(ILogger logger, string fileName, Exception exception);

    [LoggerMessage(
        EventId = 1054,
        Level = LogLevel.Warning,
        Message = "Reading tags from {FileName} exceeded the range-request budget after "
                  + "{RequestCount} request(s). Falling back to the file name rather than "
                  + "downloading the whole file.")]
    public static partial void MetadataBudgetExceeded(ILogger logger, string fileName, int requestCount);

    [LoggerMessage(
        EventId = 1055,
        Level = LogLevel.Warning,
        Message = "Metadata extraction failed for {FileName}. The sync continues; one bad file "
                  + "must not fail the pass.")]
    public static partial void MetadataFileFailed(ILogger logger, string fileName, Exception exception);

    [LoggerMessage(
        EventId = 1040,
        Level = LogLevel.Information,
        Message = "Database is up to date at {DatabaseFile}; no migrations pending.")]
    public static partial void DatabaseUpToDate(ILogger logger, string databaseFile);

    [LoggerMessage(
        EventId = 1041,
        Level = LogLevel.Information,
        Message = "Applying {Count} pending database migration(s): {Migrations}")]
    public static partial void ApplyingMigrations(ILogger logger, int count, string migrations);

    [LoggerMessage(
        EventId = 1042,
        Level = LogLevel.Information,
        Message = "Backed up the database to {BackupFile} ({Bytes} bytes) before migrating.")]
    public static partial void DatabaseBackedUp(ILogger logger, string backupFile, long bytes);

    [LoggerMessage(
        EventId = 1043,
        Level = LogLevel.Warning,
        Message = "Could not back up the database before migrating. Continuing, because refusing "
                  + "to start would be worse than starting without a backup.")]
    public static partial void DatabaseBackupFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1044,
        Level = LogLevel.Warning,
        Message = "Storage:AutoMigrate is disabled and {Count} migration(s) are pending. The "
                  + "application will run against an out-of-date schema and is likely to fail.")]
    public static partial void MigrationsPendingButDisabled(ILogger logger, int count);

    [LoggerMessage(
        EventId = 1045,
        Level = LogLevel.Debug,
        Message = "Pruned {Count} old database backup(s).")]
    public static partial void BackupsPruned(ILogger logger, int count);
}

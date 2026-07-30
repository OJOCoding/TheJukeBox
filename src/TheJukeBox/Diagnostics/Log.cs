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
        EventId = 1030,
        Level = LogLevel.Debug,
        Message = "Drive listing returned {Count} audio file(s).")]
    public static partial void DriveListed(ILogger logger, int count);

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

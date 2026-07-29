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
        EventId = 1030,
        Level = LogLevel.Debug,
        Message = "Drive listing returned {Count} audio file(s).")]
    public static partial void DriveListed(ILogger logger, int count);
}

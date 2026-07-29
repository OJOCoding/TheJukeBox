using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TheJukeBox.Diagnostics;

namespace TheJukeBox.Errors;

/// <summary>
/// The single place an unhandled exception becomes an HTTP response.
/// </summary>
internal sealed class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // A cancelled request is the client hanging up, not a fault. Writing a
        // response body here is pointless and logging it as an error is noise --
        // and this happens constantly once audio streaming lands, because skipping
        // a track aborts an in-flight range request by design.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            // Guarded because PathString -> string allocates, and this path is hit
            // constantly once audio streaming lands: every skipped track aborts an
            // in-flight range request.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                var path = httpContext.Request.Path.Value ?? "/";
                Log.RequestAborted(logger, path);
            }

            return true;
        }

        var problem = Describe(exception);

        if (problem.Status >= StatusCodes.Status500InternalServerError)
        {
            Log.Unhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            Log.ClientProblem(logger, problem.Type, httpContext.Request.Method,
                httpContext.Request.Path, exception.Message);
        }

        httpContext.Response.StatusCode = problem.Status!.Value;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem,
        });
    }

    private static ProblemDetails Describe(Exception exception) => exception switch
    {
        JukeBoxException known => new ProblemDetails
        {
            Type = known.ProblemType,
            Title = known.Title,
            Status = known.StatusCode,
            Detail = known.MessageIsSafeToExpose ? known.Message : null,
        },

        // Everything below is a bug or an unanticipated upstream failure. The
        // client gets a traceId and nothing else; the message goes to the log.
        _ => new ProblemDetails
        {
            Type = ProblemTypes.Internal,
            Title = "An unexpected error occurred.",
            Status = StatusCodes.Status500InternalServerError,
            Detail = null,
        },
    };

    /// <summary>
    /// Adds the fields every problem response should carry, whether it came from
    /// this handler or from the framework's own status-code pages.
    /// </summary>
    public static void Enrich(ProblemDetailsContext context)
    {
        context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;

        // Activity.Current is the W3C trace id when tracing is on; the ASP.NET Core
        // request id is the fallback. Either lets a user quote one token that finds
        // the corresponding log entry.
        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;

        context.ProblemDetails.Type ??= context.ProblemDetails.Status switch
        {
            StatusCodes.Status400BadRequest => ProblemTypes.Validation,
            StatusCodes.Status401Unauthorized => ProblemTypes.Unauthenticated,
            StatusCodes.Status404NotFound => ProblemTypes.NotFound,
            _ => ProblemTypes.Internal,
        };
    }
}

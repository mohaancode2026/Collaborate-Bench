using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Infrastructure;

// =====================================================================================================================
// Error handling conventions (read this before adding a try/catch)
//
//  1. Expected problems — bad input, missing record, no permission, business rule — are signalled by throwing
//     AppException (or using Validator). Controllers do NOT catch them; GlobalExceptionHandler turns them into
//     ProblemDetails with the message written for the end user.
//  2. Catch an exception only when you can do something specific with it, and catch the narrowest type:
//       - translate it into an AppException the user understands   (e.g. DbUpdateException when SqlErrors.IsDuplicate)
//       - fall back to another path                                (e.g. Claude unavailable → built-in engine)
//       - keep a background loop alive                             (log, then continue with the next item)
//     Never catch-and-ignore without a comment saying why it is safe.
//  3. Everything else bubbles up to GlobalExceptionHandler, which logs it once with the correlation id and returns a
//     safe 500 without internal details.
//  4. Every error body has the same shape:
//       { status, title, detail, instance, code, correlationId, errors? }   (Content-Type: application/problem+json)
//     "code" is stable for the frontend; "title"/"detail" are for people; "errors" holds field → messages for forms.
// =====================================================================================================================

/// <summary>An error the user should see. Rendered as RFC 7807 ProblemDetails with a stable <see cref="Code"/>.</summary>
public sealed class AppException : Exception
{
    /// <summary>HTTP status to return (4xx for the caller's problem, 5xx for ours or a dependency's).</summary>
    public int Status { get; }
    /// <summary>Machine-readable code the frontend can switch on, e.g. VALIDATION, NOT_FOUND, DUPLICATE_SUBMISSION.</summary>
    public string Code { get; }
    /// <summary>Short headline for the user. The exception message is the longer explanation (ProblemDetails "detail").</summary>
    public string Title { get; }
    /// <summary>Field name → messages, for showing errors next to form fields.</summary>
    public IDictionary<string, string[]>? Errors { get; init; }

    public AppException(int status, string code, string title, string detail) : base(detail)
    {
        Status = status; Code = code; Title = title;
    }

    public static AppException Forbidden(string detail) => new(403, "PERMISSION_DENIED", "Permission denied", detail);
    public static AppException NotFound(string what) =>
        new(404, "NOT_FOUND", $"{what} not found", $"The {what.ToLowerInvariant()} doesn't exist, was removed, or is outside your team.");
    public static AppException Conflict(string code, string title, string detail) => new(409, code, title, detail);
    public static AppException Bad(string code, string title, string detail) => new(400, code, title, detail);
}

/// <summary>Collects field errors and throws one VALIDATION error with all of them, so the user fixes everything in one go.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = new();

    /// <summary>Records <paramref name="message"/> against <paramref name="field"/> when <paramref name="ok"/> is false.</summary>
    public Validator Check(bool ok, string field, string message)
    {
        if (!ok)
        {
            if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = new List<string>();
            list.Add(message);
        }
        return this;
    }

    /// <summary>Throws a 400 VALIDATION <see cref="AppException"/> if any check failed.</summary>
    public void ThrowIfInvalid()
    {
        if (_errors.Count == 0) return;
        throw new AppException(400, "VALIDATION", "Please fix the highlighted fields",
            _errors.Count == 1 ? "1 field needs attention." : $"{_errors.Count} fields need attention.")
        { Errors = _errors.ToDictionary(k => k.Key, v => v.Value.ToArray()) };
    }

    public static bool IsEmail(string? s) =>
        !string.IsNullOrWhiteSpace(s) && System.Text.RegularExpressions.Regex.IsMatch(s, @"^[^\s@]+@[^\s@]+\.[^\s@]{2,}$");
}

/// <summary>Writes the standard error body. Used by the exception handler, auth, rate limiting and 404/405 pages.</summary>
public static class Problem
{
    /// <summary>
    /// Writes ProblemDetails with <c>code</c> and <c>correlationId</c> (the request's id from <see cref="CorrelationMiddleware"/>,
    /// which also appears in the logs and the X-Correlation-Id header).
    /// </summary>
    public static Task WriteAsync(HttpContext ctx, int status, string code, string title, string detail, IDictionary<string, string[]>? errors = null)
    {
        var pd = new ProblemDetails { Status = status, Title = title, Detail = detail, Instance = ctx.Request.Path };
        pd.Extensions["code"] = code;
        pd.Extensions["correlationId"] = ctx.TraceIdentifier;
        if (errors != null) pd.Extensions["errors"] = errors;
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(pd, (JsonSerializerOptions?)null, "application/problem+json");
    }
}

/// <summary>
/// Last line of defence: turns any exception that escapes a controller or middleware into a ProblemDetails response.
/// <para>
/// Logging: 4xx is logged once at Information without a stack trace (the request broke a rule; nothing is wrong with the
/// server); 5xx is logged once at Error with the full exception. Every line carries the correlation id. The framework's own
/// "unhandled exception" log is switched off in appsettings so errors aren't logged twice.
/// </para>
/// Users never see exception messages, SQL text or stack traces — only the messages written in <see cref="AppException"/>s.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var method = ctx.Request.Method;
        var path = ctx.Request.Path.Value;

        // The browser went away (tab closed, navigation). Nobody is waiting for an answer, so there is nothing to log or write.
        if (ex is OperationCanceledException && ctx.RequestAborted.IsCancellationRequested)
        {
            log.LogDebug("{Method} {Path} cancelled by the client", method, path);
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 499;   // nginx convention for "client closed request"
            return true;
        }

        var error = Classify(ex, ctx.TraceIdentifier);
        var sqlNumber = SqlErrors.Find(ex)?.Number;
        if (error.Status >= 500)
            log.LogError(ex, "{Method} {Path} failed: {Status} {Code}{Sql}", method, path, error.Status, error.Code, sqlNumber is int n ? $" (SQL {n})" : "");
        else if (sqlNumber is int sn)
            log.LogWarning("{Method} {Path} rejected by the database: {Status} {Code} (SQL {Number})", method, path, error.Status, error.Code, sn);
        else
            log.LogInformation("{Method} {Path} returned {Status} {Code}: {Detail}", method, path, error.Status, error.Code, error.Message);

        if (ctx.Response.HasStarted)
        {
            // Part of the body was already sent (e.g. a file download failed half-way). A JSON error can't be added now,
            // so cut the connection; the client sees a failed download instead of a corrupt file.
            log.LogWarning("{Method} {Path}: response already started, aborting the connection", method, path);
            ctx.Abort();
            return true;
        }

        if (error.Status == 503) ctx.Response.Headers.RetryAfter = "10";
        await Problem.WriteAsync(ctx, error.Status, error.Code, error.Title, error.Message, error.Errors);
        return true;
    }

    /// <summary>Decides the status, code and user-facing message for an exception. Order matters: most specific first.</summary>
    private static AppException Classify(Exception ex, string reference) => ex switch
    {
        // Thrown on purpose by our code: already has the right status and message.
        AppException a => a,

        // Kestrel / model binding: body too big or unreadable.
        BadHttpRequestException { StatusCode: 413 } => new(413, "PAYLOAD_TOO_LARGE", "Request too large",
            "What you sent is too large. Attachments must be under 3 MB in total."),
        BadHttpRequestException b => new(b.StatusCode is >= 400 and < 500 ? b.StatusCode : 400, "BAD_REQUEST", "Invalid request",
            "The request could not be read. Check the data you sent and try again."),
        JsonException => new(400, "BAD_REQUEST", "Invalid request", "The data sent isn't valid JSON."),

        // SQL Server and EF Core: business rules from stored procedures, duplicates, concurrency, connection problems.
        _ when SqlErrors.Map(ex) is { } mapped => mapped,
        DbUpdateException => new(500, "DB_ERROR", "Couldn't save your change",
            $"The change couldn't be saved. Please try again. If it keeps happening, give your admin this reference: {reference}."),

        // Something we called took too long or failed (an outside API, or a query that hit its timeout).
        TimeoutException or OperationCanceledException => new(504, "TIMEOUT", "The request timed out",
            "This took too long to finish. Please try again in a moment."),
        HttpRequestException => new(502, "UPSTREAM_ERROR", "A connected service failed",
            "A service this feature depends on didn't respond properly. Please try again in a minute."),

        // A bug. Say as little as possible, but give the user something to quote.
        _ => new(500, "SERVER_ERROR", "Something went wrong",
            $"The request could not be completed. Please try again. If it keeps happening, give your admin this reference: {reference}."),
    };
}

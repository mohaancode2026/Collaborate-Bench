using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CollaborateBench.Api.Infrastructure;

/// <summary>
/// Gives every request a single correlation id so a problem a user reports can be found in the logs.
/// <list type="bullet">
/// <item>Reused from the incoming <c>X-Correlation-Id</c> header when it looks safe (a gateway or the frontend may set it), otherwise generated.</item>
/// <item>Stored in <see cref="HttpContext.TraceIdentifier"/>, which <see cref="Problem"/> puts in every error body as <c>correlationId</c>.</item>
/// <item>Returned in the <c>X-Correlation-Id</c> response header, and attached as a logging scope to every log line written for the request.</item>
/// </list>
/// Must be the first middleware so that even errors raised by later middleware carry the id.
/// </summary>
public sealed partial class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> log)
{
    public const string Header = "X-Correlation-Id";

    // Only short, plain ids are accepted from callers, so a header can't inject text into logs.
    [GeneratedRegex(@"^[A-Za-z0-9._-]{8,64}$")]
    private static partial Regex SafeId();

    public static string NewId() => "REQ-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5));

    public async Task InvokeAsync(HttpContext ctx)
    {
        var incoming = ctx.Request.Headers[Header].ToString();
        var id = SafeId().IsMatch(incoming) ? incoming : NewId();
        ctx.TraceIdentifier = id;
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers[Header] = id;
            return Task.CompletedTask;
        });

        // Message-template scope: readable in the console ("CorrelationId:REQ-…") and a structured property in log stores.
        using (log.BeginScope("CorrelationId:{CorrelationId}", id))
            await next(ctx);
    }
}

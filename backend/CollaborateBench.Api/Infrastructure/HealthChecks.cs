using System.Text.Json;
using CollaborateBench.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CollaborateBench.Api.Infrastructure;

/// <summary>
/// Readiness check: can the API reach SQL Server within 5 seconds? Exposed at <c>/health/ready</c> for Docker, IIS,
/// load balancers and monitoring. <c>/health</c> (liveness) only says the process is running and never touches the database.
/// </summary>
public sealed class DatabaseHealthCheck(IDbContextFactory<AppDbContext> factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var db = await factory.CreateDbContextAsync(timeout.Token);
            return await db.Database.CanConnectAsync(timeout.Token)
                ? HealthCheckResult.Healthy("SQL Server reachable")
                : HealthCheckResult.Unhealthy("SQL Server not reachable");
        }
        // A failed probe is a result, not an error: report it (the exception is logged by the health check service).
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(timeout.IsCancellationRequested ? "SQL Server didn't answer within 5 seconds" : "SQL Server not reachable", ex);
        }
    }
}

public static class HealthResponse
{
    /// <summary>Small JSON body: overall status plus each check's status and time. No exception text, so nothing internal leaks.</summary>
    public static Task WriteAsync(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            totalMs = (int)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Description, ms = (int)e.Value.Duration.TotalMilliseconds }),
        }));
    }
}

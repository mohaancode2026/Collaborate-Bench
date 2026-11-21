using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

// Collaborate-Bench Sales API.
// Layers:  Controllers (HTTP, permissions, input validation)  →  Services (business rules, integrations)
//          →  Data (EF Core entities + AppDbContext, team filters)  →  SQL Server.
// Errors:  throw AppException / use Validator; GlobalExceptionHandler turns everything into ProblemDetails (see Infrastructure/Errors.cs).

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApi(builder.Configuration)
    .AddPersistence(builder.Configuration, builder.Environment)
    .AddApplicationServices(builder.Configuration)
    .AddIntegrations(builder.Configuration)
    .AddSecurity()
    .AddRateLimits()
    .AddApiDocs(builder.Configuration, builder.Environment);   // Swagger at /swagger (Development, or Swagger:Enabled=true)

var app = builder.Build();

// ---------- HTTP pipeline (order matters) ----------
app.UseMiddleware<CorrelationMiddleware>();   // 1. correlation id first, so every log line and error body carries it
app.UseExceptionHandler();                     // 2. catches anything thrown below and writes ProblemDetails
app.UseStatusCodePages(async ctx =>            // 3. bodiless 404/405 from routing get the same error shape
{
    var r = ctx.HttpContext.Response;
    if (r.StatusCode == 404 && ctx.HttpContext.Request.Path.StartsWithSegments("/api"))
        await Problem.WriteAsync(ctx.HttpContext, 404, "ENDPOINT_NOT_FOUND", "Not found", "That API endpoint doesn't exist.");
    else if (r.StatusCode == 405)
        await Problem.WriteAsync(ctx.HttpContext, 405, "METHOD_NOT_ALLOWED", "Not allowed", "That action isn't supported here.");
});
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseApiDocs();                              // /swagger UI and /swagger/v1/swagger.json (before the SPA fallback)

app.UseDefaultFiles();
app.UseStaticFiles();                          // serves the built React app from wwwroot in production
app.UseCors("web");
app.UseAuthentication();
app.UseRateLimiter();                          // after authentication, so the "ai" limit is per user
app.UseAuthorization();

app.MapControllers();
// Liveness: the process is up (no dependencies checked). Readiness: SQL Server answers.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = HealthResponse.WriteAsync }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"), ResponseWriter = HealthResponse.WriteAsync }).AllowAnonymous();
app.MapFallback("/api/{**rest}", ctx => Problem.WriteAsync(ctx, 404, "ENDPOINT_NOT_FOUND", "Not found", "That API endpoint doesn't exist.")).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

await SeedDemoPasswordsAsync(app);
app.Run();

// Development convenience: give seeded users the demo password. Never enable Seed:SetDemoPasswords in production.
static async Task SeedDemoPasswordsAsync(WebApplication app)
{
    if (!app.Configuration.GetValue<bool>("Seed:SetDemoPasswords")) return;
    using var scope = app.Services.CreateScope();
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = new PasswordHasher<string>();
        var users = await db.Users.Where(u => u.PasswordHash == null).ToListAsync();
        foreach (var u in users) u.PasswordHash = hasher.HashPassword(u.Email, "Demo@123");
        await db.SaveChangesAsync();
        if (users.Count > 0) log.LogWarning("Set demo password Demo@123 for {Count} users. Disable Seed:SetDemoPasswords in production.", users.Count);
    }
    // Startup must not fail just because the optional demo seeding couldn't run (e.g. the database isn't created yet).
    catch (Exception ex) { log.LogError(ex, "Demo password seeding skipped. Is the database reachable and are the migrations applied?"); }
}

public partial class Program;

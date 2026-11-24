using System.Threading.RateLimiting;
using CollaborateBench.Api.Ai;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace CollaborateBench.Api.Infrastructure;

/// <summary>
/// Dependency-injection setup, grouped by concern so Program.cs reads as a table of contents.
/// <para>Lifetimes: request-scoped for anything that uses the signed-in user or a DbContext; singletons only for
/// stateless or thread-safe services (token signing, background workers).</para>
/// </summary>
public static class ServiceRegistration
{
    /// <summary>EF Core on SQL Server 2012+, with the user-tagging interceptor and a readiness health check.</summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration cfg, IWebHostEnvironment env)
    {
        var connection = cfg.GetConnectionString("Sql");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("ConnectionStrings:Sql is not configured. Set it in appsettings, user secrets or an environment variable.");

        services.AddSingleton<SessionContextInterceptor>();
        // The factory gives audit logging, health checks and background jobs their own short-lived contexts;
        // controllers and request services get the scoped one below.
        services.AddDbContextFactory<AppDbContext>((sp, o) => o
            .UseSqlServer(connection,
                // 110 = SQL Server 2012: EF must not generate OPENJSON (2016+) for list parameters.
                sql => sql.CommandTimeout(30).UseCompatibilityLevel(110))
            .AddInterceptors(sp.GetRequiredService<SessionContextInterceptor>())
            // EF logs every failed command/query/save at Error. Those exceptions are logged once by whoever handles them
            // (GlobalExceptionHandler for requests, the background service otherwise), so EF's copies go to Debug.
            .ConfigureWarnings(w => w.Log(
                (RelationalEventId.CommandError, LogLevel.Debug),
                (RelationalEventId.ConnectionError, LogLevel.Debug),
                (CoreEventId.QueryIterationFailed, LogLevel.Debug),
                (CoreEventId.SaveChangesFailed, LogLevel.Debug)))
            .EnableSensitiveDataLogging(env.IsDevelopment()));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
        return services;
    }

    /// <summary>Business services used by the controllers.</summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<DataService>();
        services.AddScoped<AuditService>();
        services.AddScoped<SubmissionService>();
        services.AddScoped<AiService>();
        services.AddScoped<ResumeTailor>();
        // Bench sales Autopilot: finds, prepares and (after recruiter approval + consultant RTR) submits; follows up and reads vendor replies.
        services.Configure<AutopilotOptions>(cfg.GetSection("Autopilot"));
        services.AddScoped<AutopilotService>();
        services.AddSingleton<AutopilotWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<AutopilotWorker>());
        services.Configure<FollowUpOptions>(cfg.GetSection("FollowUps"));
        return services;
    }

    /// <summary>
    /// Outside services. Each one is optional: without its keys the feature shows a "not set up" message and the rest of
    /// the app keeps working. All outgoing HTTP goes through named HttpClients with explicit timeouts.
    /// </summary>
    public static IServiceCollection AddIntegrations(this IServiceCollection services, IConfiguration cfg)
    {
        // Claude (Anthropic API): AI features fall back to the built-in engine when the key is missing or the API fails.
        services.AddScoped<ClaudeClient>();
        // 3 minutes: rewriting a full resume is a long answer.
        // Anthropic:BaseUrl is only for a company API gateway/proxy; it defaults to the Anthropic API.
        services.AddHttpClient("claude", c => { c.BaseAddress = new Uri(cfg["Anthropic:BaseUrl"] is { Length: > 0 } u ? u : "https://api.anthropic.com/"); c.Timeout = TimeSpan.FromSeconds(180); });

        // External job search (JSearch on RapidAPI). Off until JobFeed:RapidApiKey is set.
        services.Configure<JobFeedOptions>(cfg.GetSection("JobFeed"));
        services.AddScoped<JSearchClient>();
        services.AddScoped<DiceClient>();
        // Dice's public MCP server. A plain product name in the User-Agent; Dice tags its links with it as utm_source.
        services.AddHttpClient("dice", c => { c.Timeout = TimeSpan.FromSeconds(30); c.DefaultRequestHeaders.UserAgent.ParseAdd("CollaborateBench/1.0"); });
        services.AddScoped<JobFeedService>();
        services.AddHostedService<JobFeedRefresher>();
        services.AddHttpClient("jobfeed", (sp, c) =>
        {
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<JobFeedOptions>>().Value.BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(30);
        });

        // Sign in with Microsoft + Outlook vendor email tracking and sending. Off until Microsoft:TenantId/ClientId/ClientSecret are set.
        services.Configure<MicrosoftOptions>(cfg.GetSection("Microsoft"));
        services.AddScoped<MicrosoftAuth>();
        services.AddScoped<MailSender>();
        services.AddScoped<VendorDiscovery>();
        // AI Inbox: triages each user's Outlook Inbox during the mail sync.
        services.Configure<InboxOptions>(cfg.GetSection("Inbox"));
        services.AddScoped<InboxTriage>();
        services.AddSingleton<ResumeStore>();
        services.AddScoped<ResumeFiles>();
        services.AddScoped<MailSignatureService>();
        services.AddSingleton<MailSync>();
        services.AddHostedService(sp => sp.GetRequiredService<MailSync>());
        services.AddHttpClient("microsoft", c => c.Timeout = TimeSpan.FromSeconds(60));

        // Encrypts saved Outlook refresh tokens. Keys must survive restarts (and be shared by every server), or users reconnect Outlook.
        var dataProtection = services.AddDataProtection().SetApplicationName("CollaborateBench");
        if (cfg["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        return services;
    }

    /// <summary>Bearer tokens issued by <see cref="TokenService"/>, and one authorization policy per permission code.</summary>
    public static IServiceCollection AddSecurity(this IServiceCollection services)
    {
        services.AddSingleton<TokenService>();
        services.AddAuthentication("Bearer").AddScheme<AuthenticationSchemeOptions, BearerHandler>("Bearer", null);
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        // Secure by default: every endpoint needs a signed-in user unless it says [AllowAnonymous].
        services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder("Bearer").RequireAuthenticatedUser().Build());
        return services;
    }

    /// <summary>Controllers, the global exception handler, and the same VALIDATION error shape for model-binding failures.</summary>
    public static IServiceCollection AddApi(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddControllers().ConfigureApiBehaviorOptions(o =>
        {
            // Malformed JSON or wrong types never reach the action: they get the same VALIDATION body as our own checks.
            o.InvalidModelStateResponseFactory = ctx =>
            {
                // An upload over the size limit surfaces here, not in the exception handler: the form binder turns the
                // limit exception into a plain model error ("Failed to read the request form. Request body too large…").
                static bool TooLarge(Microsoft.AspNetCore.Mvc.ModelBinding.ModelError e) =>
                    e.Exception is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } or InvalidDataException
                    || e.ErrorMessage.Contains("body too large", StringComparison.OrdinalIgnoreCase)
                    || e.ErrorMessage.Contains("body length limit", StringComparison.OrdinalIgnoreCase);
                var tooLarge = ctx.ModelState.Values.SelectMany(v => v.Errors).Any(TooLarge);
                if (tooLarge)
                {
                    var big = new ProblemDetails { Status = 413, Title = "Request too large", Detail = "What you sent is too large. Attachments must be under 3 MB in total.", Instance = ctx.HttpContext.Request.Path };
                    big.Extensions["code"] = "PAYLOAD_TOO_LARGE";
                    big.Extensions["correlationId"] = ctx.HttpContext.TraceIdentifier;
                    return new ObjectResult(big) { StatusCode = 413, ContentTypes = { "application/problem+json" } };
                }

                static string Field(string key)
                {
                    var k = key.TrimStart('$', '.');
                    return k.Length == 0 ? "body" : char.ToLowerInvariant(k[0]) + k[1..];
                }
                var errors = ctx.ModelState.Where(kv => kv.Value?.Errors.Count > 0)
                    .ToDictionary(kv => Field(kv.Key), kv => kv.Value!.Errors.Select(_ => "This value is missing or has the wrong format").ToArray());
                var pd = new ProblemDetails
                {
                    Status = 400, Title = "Please fix the highlighted fields", Detail = "Some values are missing or have the wrong format.",
                    Instance = ctx.HttpContext.Request.Path,
                };
                pd.Extensions["code"] = "VALIDATION";
                pd.Extensions["errors"] = errors;
                pd.Extensions["correlationId"] = ctx.HttpContext.TraceIdentifier;
                return new BadRequestObjectResult(pd) { ContentTypes = { "application/problem+json" } };
            };
        });

        services.AddCors(o => o.AddPolicy("web", p => p
            .WithOrigins(cfg.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
            .AllowAnyHeader().AllowAnyMethod()
            .WithExposedHeaders(CorrelationMiddleware.Header, "Content-Disposition")));
        return services;
    }

    /// <summary>"login": 10 per minute per IP (brute force). "ai": 30 per minute per user (cost control on paid APIs).</summary>
    public static IServiceCollection AddRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(o =>
        {
            o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
            o.AddPolicy("ai", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
            o.OnRejected = (ctx, _) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                return new ValueTask(Problem.WriteAsync(ctx.HttpContext, 429, "RATE_LIMITED", "Too many requests",
                    "You're going a bit fast. Wait a minute and try again."));
            };
        });
        return services;
    }
}

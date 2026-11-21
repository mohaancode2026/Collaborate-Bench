using System.Reflection;
using CollaborateBench.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace CollaborateBench.Api.Infrastructure;

/// <summary>
/// Swagger / OpenAPI documentation at <c>/swagger</c>. Built from the controllers and their XML doc comments.
/// <para>On in Development, off elsewhere unless <c>Swagger:Enabled</c> is true. The docs list every endpoint and its
/// permission, so think before exposing them on a public server.</para>
/// </summary>
public static class ApiDocs
{
    private const string DocName = "v1";

    public static bool Enabled(IConfiguration cfg, IWebHostEnvironment env) => cfg.GetValue("Swagger:Enabled", env.IsDevelopment());

    public static IServiceCollection AddApiDocs(this IServiceCollection services, IConfiguration cfg, IWebHostEnvironment env)
    {
        if (!Enabled(cfg, env)) return services;
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc(DocName, new OpenApiInfo
            {
                Title = "Collaborate-Bench Sales API",
                Version = "v1",
                Description = """
                    Bench sales platform: consultants, requirements, matching, submissions, onboarding, vendor email and AI helpers.

                    **Sign in:** call `POST /api/auth/login` with your email and password, copy `token` from the response,
                    click **Authorize** and paste it. Every endpoint needs a token unless it is marked as public.

                    **Permissions:** each operation lists the permission it needs (e.g. `bench.view`). Data is also limited to your
                    data scope: Recruiters see their own records, Team Leads their team's, managers and admins everyone's.

                    **Errors:** every error is `application/problem+json` with a stable `code`, a `title`/`detail` written for users,
                    `errors` (field → messages) for validation, and a `correlationId` that also appears in the `X-Correlation-Id`
                    header and the server logs. Quote it when reporting a problem.

                    **Rate limits:** sign-in 10/minute per IP; AI, job search and email sending 30/minute per user (429 with `Retry-After`).
                    """,
            });

            var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xml)) c.IncludeXmlComments(xml, includeControllerXmlComments: true);

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header,
                Description = "Token from POST /api/auth/login (or Sign in with Microsoft). Paste only the token, without \"Bearer \".",
            });
            c.OperationFilter<ApiConventionsFilter>();
            c.SupportNonNullableReferenceTypes();
            // Nested types (e.g. Rules+Something) and same-named types in different namespaces get unique schema ids.
            c.CustomSchemaIds(t => (t.FullName ?? t.Name).Replace("CollaborateBench.Api.", "").Replace("Models.", "").Replace('+', '.'));
        });
        return services;
    }

    public static WebApplication UseApiDocs(this WebApplication app)
    {
        if (!Enabled(app.Configuration, app.Environment)) return app;
        app.UseSwagger();
        app.UseSwaggerUI(o =>
        {
            o.SwaggerEndpoint($"/swagger/{DocName}/swagger.json", "Collaborate-Bench Sales API v1");
            o.DocumentTitle = "Collaborate-Bench API";
            o.EnablePersistAuthorization();   // keeps the pasted token across page reloads (browser storage, this machine only)
            o.DisplayRequestDuration();
            o.EnableFilter();
            o.DefaultModelsExpandDepth(0);    // schemas collapsed; the operations are what people come for
        });
        return app;
    }
}

/// <summary>The error body every endpoint returns (documentation only; the real one is written by <see cref="Problem"/>).</summary>
public sealed class ApiProblem
{
    /// <example>409</example>
    public int Status { get; set; }
    /// <example>Already submitted</example>
    public string Title { get; set; } = "";
    /// <example>Ravi Kumar has already been submitted to "Senior Java Developer". Duplicate submissions break the RTR with the vendor.</example>
    public string Detail { get; set; } = "";
    /// <example>/api/submissions</example>
    public string? Instance { get; set; }
    /// <summary>Stable machine-readable code, e.g. VALIDATION, NOT_FOUND, PERMISSION_DENIED, DUPLICATE_SUBMISSION.</summary>
    /// <example>DUPLICATE_SUBMISSION</example>
    public string Code { get; set; } = "";
    /// <summary>Same value as the X-Correlation-Id header and the server log lines for this request.</summary>
    /// <example>REQ-7F3A91C2D4</example>
    public string CorrelationId { get; set; } = "";
    /// <summary>Validation only: field name → messages.</summary>
    public Dictionary<string, string[]>? Errors { get; set; }
}

/// <summary>
/// Adds the API's cross-cutting rules to each operation, read from the endpoint's attributes:
/// Bearer security (unless [AllowAnonymous]), the required permission ([RequirePerm]), the rate limit, and the standard
/// error responses (400/401/403/404/413/429/500) with the <see cref="ApiProblem"/> body.
/// </summary>
public sealed class ApiConventionsFilter : IOperationFilter
{
    public void Apply(OpenApiOperation op, OperationFilterContext ctx)
    {
        var meta = ctx.ApiDescription.ActionDescriptor.EndpointMetadata;
        var anonymous = meta.OfType<IAllowAnonymous>().Any();
        var perms = meta.OfType<RequirePermAttribute>().Select(p => p.Policy!["perm:".Length..]).Distinct().ToList();
        var limit = meta.OfType<EnableRateLimitingAttribute>().LastOrDefault()?.PolicyName;
        var method = ctx.ApiDescription.HttpMethod ?? "GET";
        var path = ctx.ApiDescription.RelativePath ?? "";

        var notes = new List<string>();
        if (anonymous) notes.Add("**Public:** no token needed.");
        if (perms.Count > 0) notes.Add("**Requires permission:** " + string.Join(", ", perms.Select(p => $"`{p}` ({Perm.Label(p)})")));
        else if (!anonymous) notes.Add("**Requires:** any signed-in user.");
        if (limit != null) notes.Add(limit == "login" ? "**Rate limit:** 10 per minute per IP." : "**Rate limit:** 30 per minute per user.");
        op.Description = string.Join("\n\n", new[] { op.Description }.Where(d => !string.IsNullOrWhiteSpace(d)).Concat([string.Join("<br/>", notes)]));

        if (!anonymous)
            op.Security = [new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [] }];

        var problem = ctx.SchemaGenerator.GenerateSchema(typeof(ApiProblem), ctx.SchemaRepository);
        void Error(int status, string description, string code)
        {
            var key = status.ToString();
            if (op.Responses.ContainsKey(key)) return;
            op.Responses[key] = new OpenApiResponse
            {
                Description = description,
                Content = { ["application/problem+json"] = new OpenApiMediaType { Schema = problem, Example = new OpenApiObject { ["status"] = new OpenApiInteger(status), ["code"] = new OpenApiString(code), ["correlationId"] = new OpenApiString("REQ-7F3A91C2D4") } } },
            };
        }

        if (method is "POST" or "PUT" or "PATCH" || ctx.ApiDescription.ParameterDescriptions.Any())
            Error(400, "Validation failed (`VALIDATION`, with `errors`) or the request couldn't be read.", "VALIDATION");
        if (!anonymous)
        {
            Error(401, "No token, or the token expired / is invalid (`UNAUTHENTICATED`, `SESSION_EXPIRED`, `INVALID_TOKEN`).", "UNAUTHENTICATED");
            if (perms.Count > 0) Error(403, $"Your role lacks `{string.Join("`/`", perms)}`, or the record is outside your team (`PERMISSION_DENIED`).", "PERMISSION_DENIED");
        }
        if (path.Contains('{')) Error(404, "Not found, or outside your team (`NOT_FOUND`).", "NOT_FOUND");
        if (ctx.ApiDescription.ParameterDescriptions.Any(p => p.Type == typeof(IFormFile) || p.Type == typeof(List<IFormFile>) || p.Source.Id == "Form"))
            Error(413, "Upload too large (`PAYLOAD_TOO_LARGE`).", "PAYLOAD_TOO_LARGE");
        if (limit != null) Error(429, "Too many requests (`RATE_LIMITED`); see the `Retry-After` header.", "RATE_LIMITED");
        Error(500, "Unexpected error (`SERVER_ERROR`). Quote the `correlationId`.", "SERVER_ERROR");
    }
}

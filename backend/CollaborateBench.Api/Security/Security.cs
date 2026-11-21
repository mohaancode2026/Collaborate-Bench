using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CollaborateBench.Api.Security;

public static class Perm
{
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["dash.view"] = "view the dashboard", ["bench.view"] = "view the bench", ["bench.create"] = "add consultants",
        ["bench.edit"] = "edit consultants", ["bench.delete"] = "remove consultants", ["bench.reassign"] = "reassign consultants to another recruiter",
        ["parser.use"] = "use the AI resume parser", ["jobs.view"] = "view requirements", ["jobs.create"] = "post requirements",
        ["jobs.close"] = "close or reopen requirements", ["board.view"] = "search the job board", ["match.view"] = "use the match agent",
        ["pipeline.view"] = "view submissions", ["sub.create"] = "submit consultants", ["sub.move"] = "move submissions between stages",
        ["sub.withdraw"] = "withdraw submissions", ["onboard.view"] = "view onboarding", ["onboard.edit"] = "manage MSA/PO, onboarding and billing",
        ["rates.view"] = "use the rate & margin calculator", ["margin.view"] = "see margins", ["team.view"] = "view the team",
        ["team.manage"] = "manage users and roles", ["audit.view"] = "view the audit log", ["ai.copilot"] = "use the AI copilot",
        ["mail.view"] = "view vendor email activity", ["vendors.manage"] = "manage vendors and their email domains",
        ["templates.manage"] = "manage shared email templates",
        ["autopilot.manage"] = "turn the Autopilot on or off and change its settings",
    };
    public static string Label(string? code) => code != null && Labels.TryGetValue(code, out var l) ? l : code ?? "do that";
}

/// <summary>[RequirePerm("bench.create")] on an action = the user's role must hold that permission.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequirePermAttribute : AuthorizeAttribute
{
    public RequirePermAttribute(string code) { Policy = "perm:" + code; }
}

public interface ICurrentUser
{
    int? Id { get; }
    int RequiredId { get; }
    string Name { get; }
    string Role { get; }
    /// <summary>ALL | TEAM | OWN</summary>
    string DataScope { get; }
    bool Has(string perm);
    void Require(string perm);
}

public sealed class CurrentUser(IHttpContextAccessor http) : ICurrentUser
{
    private ClaimsPrincipal? P => http.HttpContext?.User;
    public int? Id => int.TryParse(P?.FindFirstValue(ClaimTypes.NameIdentifier), out var i) ? i : null;
    public int RequiredId => Id ?? throw new AppException(401, "UNAUTHENTICATED", "Please sign in", "Sign in to continue.");
    public string Name => P?.FindFirstValue(ClaimTypes.Name) ?? "";
    public string Role => P?.FindFirstValue(ClaimTypes.Role) ?? "";
    public string DataScope => P?.FindFirstValue("scope") ?? "OWN";
    public bool Has(string perm) => P?.HasClaim("perm", perm) ?? false;
    public void Require(string perm)
    {
        if (!Has(perm))
            throw AppException.Forbidden($"Your role ({Role}) can't {Perm.Label(perm)}. Ask your {(Role == "Recruiter" ? "Team Lead" : "Delivery Manager")} if you need this access.");
    }
}

/// <param name="Method">How the user signed in: "pwd" (email + password) or "microsoft".</param>
public sealed record TokenUser(int Id, string Name, string Email, string Role, string DataScope, IReadOnlyList<string> Permissions, string Method = "pwd");

/// <summary>Minimal HS256 JWT issue/validate using only the .NET base library.</summary>
public sealed class TokenService
{
    private readonly byte[] _key;
    private readonly string _issuer;
    private readonly int _minutes;

    public TokenService(IConfiguration cfg)
    {
        var key = cfg["Jwt:Key"] ?? "";
        if (key.Length < 32 || key.StartsWith("REPLACE_"))
            throw new InvalidOperationException("Jwt:Key must be set to a random secret of at least 32 characters.");
        _key = Encoding.UTF8.GetBytes(key);
        _issuer = cfg["Jwt:Issuer"] ?? "collaborate-bench";
        _minutes = cfg.GetValue("Jwt:LifetimeMinutes", 480);
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    public (string Token, DateTime ExpiresUtc) Issue(TokenUser u)
    {
        var now = DateTimeOffset.UtcNow;
        var exp = now.AddMinutes(_minutes);
        var header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payload = new JsonObject
        {
            ["iss"] = _issuer, ["sub"] = u.Id.ToString(), ["name"] = u.Name, ["email"] = u.Email,
            ["role"] = u.Role, ["scope"] = u.DataScope, ["amr"] = u.Method, ["iat"] = now.ToUnixTimeSeconds(), ["exp"] = exp.ToUnixTimeSeconds(),
            ["perms"] = new JsonArray(u.Permissions.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        };
        var body = B64(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var sig = B64(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{header}.{body}")));
        return ($"{header}.{body}.{sig}", exp.UtcDateTime);
    }

    /// <returns>principal, or null + reason (EXPIRED / INVALID)</returns>
    public (ClaimsPrincipal? Principal, string? Error) Validate(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return (null, "INVALID");
        try
        {
            var expected = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"));
            if (!CryptographicOperations.FixedTimeEquals(expected, UnB64(parts[2]))) return (null, "INVALID");
            using var doc = JsonDocument.Parse(UnB64(parts[1]));
            var p = doc.RootElement;
            if (p.GetProperty("iss").GetString() != _issuer) return (null, "INVALID");
            if (DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("exp").GetInt64()) <= DateTimeOffset.UtcNow) return (null, "EXPIRED");
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, p.GetProperty("sub").GetString()!),
                new(ClaimTypes.Name, p.GetProperty("name").GetString() ?? ""),
                new(ClaimTypes.Email, p.GetProperty("email").GetString() ?? ""),
                new(ClaimTypes.Role, p.GetProperty("role").GetString() ?? ""),
                new("scope", p.GetProperty("scope").GetString() ?? "OWN"),
                new("amr", p.TryGetProperty("amr", out var amr) ? amr.GetString() ?? "pwd" : "pwd"),
            };
            claims.AddRange(p.GetProperty("perms").EnumerateArray().Select(x => new Claim("perm", x.GetString() ?? "")));
            return (new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")), null);
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, "INVALID");
        }
    }
}

/// <summary>Bearer authentication; also rejects tokens of users deactivated after sign-in (cached 60 s).</summary>
public sealed class BearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    TokenService tokens, IMemoryCache cache, IServiceScopeFactory scopes)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();

        var (principal, error) = tokens.Validate(header[7..].Trim());
        if (principal is null)
        {
            Context.Items["authError"] = error == "EXPIRED" ? "SESSION_EXPIRED" : "INVALID_TOKEN";
            return AuthenticateResult.Fail(error ?? "INVALID");
        }

        var id = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var active = await cache.GetOrCreateAsync($"user-active:{id}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Users.AsNoTracking().Where(u => u.UserId == id).Select(u => u.IsActive).FirstOrDefaultAsync();
        });
        if (!active)
        {
            Context.Items["authError"] = "ACCOUNT_DEACTIVATED";
            return AuthenticateResult.Fail("DEACTIVATED");
        }
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var code = Context.Items["authError"] as string ?? "UNAUTHENTICATED";
        var (title, detail) = code switch
        {
            "SESSION_EXPIRED" => ("Session expired", "Your session has expired. Please sign in again."),
            "ACCOUNT_DEACTIVATED" => ("Account deactivated", "Your account has been deactivated. Contact your admin."),
            "INVALID_TOKEN" => ("Please sign in again", "Your sign-in is no longer valid. Please sign in again."),
            _ => ("Please sign in", "Sign in to continue."),
        };
        return Problem.WriteAsync(Context, 401, code, title, detail);
    }
}

public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith("perm:", StringComparison.Ordinal))
            return new AuthorizationPolicyBuilder("Bearer").RequireAuthenticatedUser().RequireClaim("perm", policyName[5..]).Build();
        return await base.GetPolicyAsync(policyName);
    }
}

/// <summary>Writes a clear 403 message naming the missing permission, and records the attempt in the audit log.</summary>
public sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext ctx, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden)
        {
            var perm = policy.Requirements.OfType<ClaimsAuthorizationRequirement>()
                .FirstOrDefault(r => r.ClaimType == "perm")?.AllowedValues?.FirstOrDefault();
            var user = ctx.RequestServices.GetRequiredService<ICurrentUser>();
            await ctx.RequestServices.GetRequiredService<AuditService>()
                .DeniedAsync($"Tried to {Perm.Label(perm)} ({ctx.Request.Method} {ctx.Request.Path})");
            await Problem.WriteAsync(ctx, 403, "PERMISSION_DENIED", "Permission denied",
                $"Your role ({user.Role}) can't {Perm.Label(perm)}. Ask your {(user.Role == "Recruiter" ? "Team Lead" : "Delivery Manager")} if you need this access.");
            return;
        }
        await _default.HandleAsync(next, ctx, policy, result);
    }
}

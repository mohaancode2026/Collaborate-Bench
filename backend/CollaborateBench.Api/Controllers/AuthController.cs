using System.Text.RegularExpressions;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;
using System.Security.Cryptography;

namespace CollaborateBench.Api.Controllers;

/// <summary>Sign-in (email + password, or Microsoft), the current user, sign-out and password changes.
/// Issues the app's own bearer tokens; Microsoft tokens never reach the browser.</summary>
[ApiController, Route("api/auth")]
public sealed class AuthController(AppDbContext db, TokenService tokens, AuditService audit, ICurrentUser me, IConfiguration cfg,
    MicrosoftAuth ms, MailSync mailSync, IMemoryCache cache, ILogger<AuthController> log) : ControllerBase
{
    private static readonly PasswordHasher<string> Hasher = new();

    private IQueryable<User> UsersWithDetails() => db.Users.Include(u => u.Role).Include(u => u.Team).Include(u => u.Manager);

    /// <param name="method">"pwd" or "microsoft". Microsoft sign-in never asks to replace a temporary password.</param>
    private async Task<UserInfo> InfoAsync(User u, string method = "pwd")
    {
        var perms = await db.RolePermissions.AsNoTracking().Where(rp => rp.RoleId == u.RoleId).Select(rp => rp.PermissionCode).ToListAsync();
        return new UserInfo(u.UserId, u.FullName, u.Email, u.Role.Name, u.Role.DataScope, u.Team?.Name, u.Manager?.FullName, perms,
            u.MustChangePassword && method == "pwd");
    }

    private async Task<LoginResponse> IssueAsync(User u, string method, string auditDetail)
    {
        var info = await InfoAsync(u, method);
        var (token, exp) = tokens.Issue(new TokenUser(u.UserId, u.FullName, u.Email, u.Role.Name, u.Role.DataScope, info.Permissions, method));
        await audit.LogAsync("LOGIN_OK", auditDetail, "User", u.UserId, userId: u.UserId);
        return new LoginResponse(token, exp, info);
    }

    /// <summary>Signs in with email and password and returns a bearer token plus the user's role and permissions.
    /// Wrong credentials and unknown emails get the same 401 so accounts can't be discovered; 5 failures lock the account for 5 minutes (423).</summary>
    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("login")]
    public async Task<LoginResponse> Login(LoginRequest req)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        new Validator()
            .Check(email.Length > 0, "email", "Enter your work email")
            .Check(email.Length == 0 || Validator.IsEmail(email), "email", "That doesn't look like a valid email address")
            .Check(!string.IsNullOrEmpty(req.Password), "password", "Enter your password")
            .ThrowIfInvalid();

        var maxFails = cfg.GetValue("Security:MaxFailedLogins", 5);
        var lockMinutes = cfg.GetValue("Security:LockoutMinutes", 5);
        var u = await UsersWithDetails().FirstOrDefaultAsync(x => x.Email == email);

        // Same message for an unknown email and a wrong password, so attackers can't discover accounts.
        if (u is null)
        {
            await audit.LogAsync("LOGIN_FAIL", $"Unknown email {email}", outcome: "Denied");
            throw new AppException(401, "INVALID_CREDENTIALS", "Incorrect email or password", "Check your email and password and try again.");
        }
        if (u.LockoutEndUtc > DateTime.UtcNow)
        {
            var mins = (int)Math.Ceiling((u.LockoutEndUtc.Value - DateTime.UtcNow).TotalMinutes);
            throw new AppException(423, "ACCOUNT_LOCKED", "Account temporarily locked",
                $"Too many failed attempts. Try again in {mins} minute{(mins == 1 ? "" : "s")}, or ask your admin to unlock it.");
        }
        if (u.PasswordHash is null)
            throw new AppException(403, "PASSWORD_NOT_SET", "Password not set", "Your account doesn't have a password yet. Ask your admin to send you a temporary password.");

        if (Hasher.VerifyHashedPassword(u.Email, u.PasswordHash, req.Password!) == PasswordVerificationResult.Failed)
        {
            var fails = u.FailedLoginCount + 1;
            if (fails >= maxFails)
            {
                u.FailedLoginCount = 0;
                u.LockoutEndUtc = DateTime.UtcNow.AddMinutes(lockMinutes);
                await db.SaveChangesAsync();
                await audit.LogAsync("LOGIN_FAIL", $"Account locked after {maxFails} failed attempts", "User", u.UserId, "Denied", u.UserId);
                throw new AppException(423, "ACCOUNT_LOCKED", "Account locked", $"Too many failed attempts. The account is locked for {lockMinutes} minutes.");
            }
            u.FailedLoginCount = (byte)fails;
            await db.SaveChangesAsync();
            await audit.LogAsync("LOGIN_FAIL", "Wrong password", "User", u.UserId, "Denied", u.UserId);
            throw new AppException(401, "INVALID_CREDENTIALS", "Incorrect email or password",
                $"{maxFails - fails} attempt{(maxFails - fails == 1 ? "" : "s")} left before the account is locked for {lockMinutes} minutes.");
        }
        if (!u.IsActive)
        {
            await audit.LogAsync("LOGIN_FAIL", "Deactivated account", "User", u.UserId, "Denied", u.UserId);
            throw new AppException(403, "ACCOUNT_DEACTIVATED", "Account deactivated", "Your account has been deactivated. Contact your admin if you think this is a mistake.");
        }

        u.FailedLoginCount = 0;
        u.LockoutEndUtc = null;
        u.LastLoginUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await IssueAsync(u, "pwd", "Signed in");
    }

    // ---------- Sign in with Microsoft (Outlook / Microsoft 365 company accounts) ----------

    /// <summary>Which sign-in options are available (whether "Sign in with Microsoft" is configured), for the login page.</summary>
    [AllowAnonymous, HttpGet("providers")]
    public object Providers() => new { microsoft = ms.Options.Configured };

    /// <summary>Returns the Microsoft sign-in URL. Mode "connect" links Outlook to the signed-in user (password users included).</summary>
    [AllowAnonymous, HttpPost("microsoft/start"), EnableRateLimiting("login")]
    public object MicrosoftStart(MsStartRequest req)
    {
        var mode = req.Mode == "connect" ? "connect" : "login";
        if (mode == "connect" && me.Id is null) throw new AppException(401, "UNAUTHENTICATED", "Please sign in", "Sign in first, then connect Outlook.");
        return new { url = ms.StartUrl(mode, me.Id, mode == "connect" ? null : req.LoginHint) };
    }

    /// <summary>Microsoft sends the browser here. The code is redeemed on the server and the browser goes back to the app.</summary>
    [AllowAnonymous, HttpGet("microsoft/callback")]
    public async Task<IActionResult> MicrosoftCallback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct)
    {
        string App(string path) => ms.Options.AppUrl.TrimEnd('/') + path;
        var st = ms.TakeState(state);
        if (st is null) return Redirect(App("/login?msError=expired"));
        var back = st.Mode == "connect" ? "/vendor-mail" : "/login";
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return Redirect(App($"{back}?msError={(error is "access_denied" or "consent_required" ? "cancelled" : "failed")}"));

        MsTokens t; string email;
        try { t = await ms.RedeemCodeAsync(code, st.Verifier, ct); (email, _) = await ms.MeAsync(t.AccessToken, ct); }
        catch (AppException ex) { log.LogWarning("Microsoft callback failed: {Error}", ex.Message); return Redirect(App($"{back}?msError=failed")); }

        if (st.Mode == "connect")
        {
            var uid = st.UserId!.Value;
            if (!await db.Users.AnyAsync(x => x.UserId == uid && x.IsActive, ct)) return Redirect(App("/login?msError=deactivated"));
            await SaveConnectionAsync(uid, email, t.RefreshToken, t.Scope, ct);
            await audit.LogAsync("MAIL_CONNECT", $"Connected Outlook mailbox {email} for vendor email tracking", "User", uid, userId: uid);
            return Redirect(App("/vendor-mail?connected=1"));
        }

        var u = await db.Users.FirstOrDefaultAsync(x => x.Email == email, ct);
        if (u is null)
        {
            await audit.LogAsync("LOGIN_FAIL", $"Microsoft sign-in by unregistered account {email}", outcome: "Denied");
            return Redirect(App("/login?msError=not_registered"));
        }
        if (!u.IsActive)
        {
            await audit.LogAsync("LOGIN_FAIL", "Microsoft sign-in to a deactivated account", "User", u.UserId, "Denied", u.UserId);
            return Redirect(App("/login?msError=deactivated"));
        }
        if (u.LockoutEndUtc > DateTime.UtcNow) return Redirect(App("/login?msError=locked"));

        u.LastLoginUtc = DateTime.UtcNow;
        u.FailedLoginCount = 0;
        await db.SaveChangesAsync(ct);
        await SaveConnectionAsync(u.UserId, email, t.RefreshToken, t.Scope, ct);

        // Hand the session to the app with a one-time code (valid 2 minutes) instead of putting a token in the URL.
        var once = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        cache.Set($"ms-login:{once}", u.UserId, TimeSpan.FromMinutes(2));
        return Redirect(App($"/login?ms={once}"));
    }

    /// <summary>Swaps the one-time code from the Microsoft callback (valid 2 minutes, single use) for a bearer token.</summary>
    [AllowAnonymous, HttpPost("microsoft/exchange"), EnableRateLimiting("login")]
    public async Task<LoginResponse> MicrosoftExchange(MsExchangeRequest req)
    {
        var key = $"ms-login:{req.Code}";
        if (string.IsNullOrEmpty(req.Code) || !cache.TryGetValue(key, out int uid))
            throw new AppException(401, "MS_CODE_EXPIRED", "Sign-in expired", "That Microsoft sign-in has expired or was already used. Sign in with Microsoft again.");
        cache.Remove(key);
        var u = await UsersWithDetails().FirstOrDefaultAsync(x => x.UserId == uid && x.IsActive)
                ?? throw new AppException(403, "ACCOUNT_DEACTIVATED", "Account deactivated", "Your account has been deactivated. Contact your admin.");
        return await IssueAsync(u, "microsoft", "Signed in with Microsoft");
    }

    private async Task SaveConnectionAsync(int userId, string mailbox, string? refreshToken, string? scopes, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken)) { log.LogWarning("Microsoft returned no refresh token for user {User}; mail tracking not connected.", userId); return; }
        var c = await db.MailConnections.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (c is null) db.MailConnections.Add(c = new MailConnection { UserId = userId });
        else if (!c.Mailbox.Equals(mailbox, StringComparison.OrdinalIgnoreCase)) c.SyncedThroughUtc = null;   // different mailbox: start over
        c.Mailbox = mailbox;
        c.RefreshTokenProtected = mailSync.Protect(refreshToken);
        c.GrantedScopes = scopes is { Length: > 1000 } ? scopes[..1000] : scopes;
        mailSync.Forget(userId);
        c.Status = "Active";
        c.LastError = null;
        await db.SaveChangesAsync(ct);
        mailSync.Kick(userId);
    }

    /// <summary>The signed-in user with role, data scope and permissions (refreshes the UI after reload).</summary>
    [HttpGet("me")]
    public async Task<UserInfo> Me()
    {
        var u = await UsersWithDetails().AsNoTracking().FirstOrDefaultAsync(x => x.UserId == me.RequiredId)
                ?? throw new AppException(401, "UNAUTHENTICATED", "Please sign in", "Your account no longer exists.");
        return await InfoAsync(u, User.FindFirstValue("amr") ?? "pwd");
    }

    /// <summary>Issues a key for the Collaborate-Bench browser extension (LinkedIn side panel), valid Jwt:ExtensionLifetimeDays (default 14).
    /// Works for password and Microsoft users alike. A key can't be used to issue another key, so it can't be renewed forever without the portal.</summary>
    [HttpPost("extension-key")]
    public async Task<LoginResponse> ExtensionKey()
    {
        if (User.FindFirstValue("amr") == "ext")
            throw AppException.Forbidden("Sign in to the portal to create a new extension key.");
        var u = await UsersWithDetails().AsNoTracking().FirstOrDefaultAsync(x => x.UserId == me.RequiredId && x.IsActive)
                ?? throw new AppException(401, "UNAUTHENTICATED", "Please sign in", "Your account no longer exists.");
        var info = await InfoAsync(u, "ext");
        var days = Math.Clamp(cfg.GetValue("Jwt:ExtensionLifetimeDays", 14), 1, 90);
        var (token, exp) = tokens.Issue(new TokenUser(u.UserId, u.FullName, u.Email, u.Role.Name, u.Role.DataScope, info.Permissions, "ext"), TimeSpan.FromDays(days));
        await audit.LogAsync("EXTENSION_KEY", $"Created a browser extension key (valid {days} days)", "User", u.UserId);
        return new LoginResponse(token, exp, info);
    }

    /// <summary>Records the sign-out in the audit log. Tokens are stateless, so the browser simply discards its copy.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await audit.LogAsync("LOGOUT", "Signed out");
        return NoContent();
    }

    public static IEnumerable<string> PasswordProblems(string? pw)
    {
        pw ??= "";
        if (pw.Length < 10) yield return "At least 10 characters";
        if (!Regex.IsMatch(pw, "[A-Z]")) yield return "An uppercase letter";
        if (!Regex.IsMatch(pw, "[a-z]")) yield return "A lowercase letter";
        if (!Regex.IsMatch(pw, "[0-9]")) yield return "A number";
        if (!Regex.IsMatch(pw, "[^A-Za-z0-9]")) yield return "A symbol";
    }

    /// <summary>Changes the password (current one required) and clears the "must change password" flag.</summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.UserId == me.RequiredId) ?? throw AppException.NotFound("User");
        var problems = PasswordProblems(req.NewPassword).ToList();
        new Validator()
            .Check(!string.IsNullOrEmpty(req.CurrentPassword), "currentPassword", "Enter your current password")
            .Check(problems.Count == 0, "newPassword", "Password needs: " + string.Join(", ", problems).ToLowerInvariant())
            .Check(req.NewPassword != req.CurrentPassword, "newPassword", "Choose a password different from the current one")
            .ThrowIfInvalid();
        if (u.PasswordHash is null || Hasher.VerifyHashedPassword(u.Email, u.PasswordHash, req.CurrentPassword!) == PasswordVerificationResult.Failed)
            throw new AppException(400, "VALIDATION", "Please fix the highlighted fields", "Your current password is incorrect.")
            { Errors = new Dictionary<string, string[]> { ["currentPassword"] = ["Current password is incorrect"] } };

        u.PasswordHash = Hasher.HashPassword(u.Email, req.NewPassword!);
        u.MustChangePassword = false;
        await db.SaveChangesAsync();
        await audit.LogAsync("PASSWORD_CHANGED", "Changed password", "User", u.UserId);
        return NoContent();
    }
}

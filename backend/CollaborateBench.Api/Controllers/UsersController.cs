using System.Security.Cryptography;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Controllers;

/// <summary>Team &amp; Access: users, roles, reporting lines and passwords. Changes rebuild the hierarchy that drives team visibility.</summary>
[ApiController, Route("api/users")]
public sealed class UsersController(AppDbContext db, DataService data, ICurrentUser me, AuditService audit) : ControllerBase
{
    private static readonly PasswordHasher<string> Hasher = new();
    private static readonly Dictionary<string, byte> RoleIds = new() { ["Admin"] = 1, ["Delivery Manager"] = 2, ["Team Lead"] = 3, ["Recruiter"] = 4 };

    /// <summary>Users you may see, with their workload (consultants, active submissions, placements).</summary>
    [HttpGet, RequirePerm("team.view")]
    public Task<List<TeamMemberDto>> List() =>
        data.VisibleUsers()
            .OrderByDescending(u => u.Role.Rank).ThenBy(u => u.FullName)
            .Select(u => new TeamMemberDto
            {
                Id = u.UserId, Name = u.FullName, Email = u.Email, Role = u.Role.Name, ManagerId = u.ManagerUserId,
                ManagerName = u.Manager != null ? u.Manager.FullName : null, Team = u.Team != null ? u.Team.Name : null,
                Active = u.IsActive, Locked = u.LockoutEndUtc > DateTime.UtcNow, LastLoginUtc = u.LastLoginUtc,
                // These counts go through row-level security, so they only include rows the caller may see.
                Consultants = db.Consultants.Count(c => c.OwnerUserId == u.UserId && c.Status != "Inactive"),
                ActiveSubmissions = db.Submissions.Count(s => s.OwnerUserId == u.UserId && s.Stage != "Billing" && s.Stage != "Withdrawn"),
                Placed = db.Submissions.Count(s => s.OwnerUserId == u.UserId && s.Stage == "Billing"),
            })
            .ToListAsync();

    /// <summary>Roles with their data scope and permissions, for the access matrix.</summary>
    [HttpGet("roles"), RequirePerm("team.view")]
    public async Task<object> Roles()
    {
        var roles = await db.Roles.AsNoTracking().OrderByDescending(r => r.Rank).Select(r => new { id = r.RoleId, name = r.Name, scope = r.DataScope }).ToListAsync();
        var perms = await db.Permissions.AsNoTracking().OrderBy(p => p.PermissionCode).Select(p => new { code = p.PermissionCode, label = p.Description }).ToListAsync();
        var grants = await db.RolePermissions.AsNoTracking().Select(rp => new { role = rp.Role.Name, perm = rp.PermissionCode }).ToListAsync();
        return new { roles, permissions = perms, grants = grants.GroupBy(g => g.role).ToDictionary(g => g.Key, g => g.Select(x => x.perm).ToList()) };
    }

    private async Task<(byte RoleId, int? TeamId)> ValidateAsync(UserInput input, int? editingId)
    {
        var email = (input.Email ?? "").Trim().ToLowerInvariant();
        var role = input.Role ?? "";
        var v = new Validator()
            .Check(!string.IsNullOrWhiteSpace(input.Name), "name", "Name is required")
            .Check(Validator.IsEmail(email), "email", "Enter a valid work email")
            .Check(RoleIds.ContainsKey(role), "role", "Choose a role");
        if (me.Role != "Admin")
            v.Check(role is "Team Lead" or "Recruiter", "role", "Only an Admin can create Admins or Delivery Managers");
        v.ThrowIfInvalid();

        v.Check(!await db.Users.AnyAsync(u => u.Email == email && u.UserId != (editingId ?? 0)), "email", "Another user already has this email");
        var manager = input.ManagerId is int mid
            ? await db.Users.AsNoTracking().Where(u => u.UserId == mid && u.IsActive).Select(u => new { u.Role.Name, u.TeamId }).FirstOrDefaultAsync()
            : null;
        if (role == "Recruiter") v.Check(manager?.Name == "Team Lead", "managerId", "A Recruiter must report to an active Team Lead");
        if (role == "Team Lead")
        {
            v.Check(manager?.Name == "Delivery Manager", "managerId", "A Team Lead must report to an active Delivery Manager");
            v.Check(!string.IsNullOrWhiteSpace(input.Team), "team", "Give the team a name");
        }
        v.Check(input.ManagerId is null || input.ManagerId != editingId, "managerId", "A user can't report to themselves");
        v.ThrowIfInvalid();

        int? teamId = role == "Recruiter" ? manager?.TeamId : null;
        if (role == "Team Lead")
        {
            var name = input.Team!.Trim();
            var team = await db.Teams.FirstOrDefaultAsync(t => t.Name == name);
            if (team is null) { team = new Team { Name = name }; db.Teams.Add(team); await db.SaveChangesAsync(); }
            teamId = team.TeamId;
        }
        return (RoleIds[role], teamId);
    }

    private static string TempPassword() =>
        "Cb!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).Replace('+', 'x').Replace('/', 'y') + "7a";

    private Task RebuildHierarchyAsync() => db.Database.ExecuteSqlRawAsync("EXEC sec.usp_RebuildHierarchy");

    /// <summary>Creates a user with a one-time temporary password (returned once; they must change it at first sign-in).</summary>
    [HttpPost, RequirePerm("team.manage")]
    public async Task<object> Create(UserInput input)
    {
        var (roleId, teamId) = await ValidateAsync(input, null);
        var email = input.Email!.Trim().ToLowerInvariant();
        var temp = TempPassword();
        var user = new User
        {
            Email = email, FullName = input.Name!.Trim(), RoleId = roleId, ManagerUserId = input.ManagerId, TeamId = teamId,
            PasswordHash = Hasher.HashPassword(email, temp), MustChangePassword = true,
        };
        db.Users.Add(user);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException e) when (SqlErrors.IsDuplicate(e))
        {
            throw new AppException(400, "VALIDATION", "Please fix the highlighted fields", "Another user already has this email.")
            { Errors = new Dictionary<string, string[]> { ["email"] = ["Another user already has this email"] } };
        }
        await RebuildHierarchyAsync();
        await audit.LogAsync("USER_CREATE", $"Created {user.FullName} ({input.Role})", "User", user.UserId);
        return new { id = user.UserId, temporaryPassword = temp, message = "Share the temporary password securely. The user must change it at first sign-in." };
    }

    /// <summary>Updates a user. You can't change your own role or deactivate yourself; deactivating hands the leaver's consultants
    /// and submissions to their manager in one transaction. People who still manage others must have their reports moved first.</summary>
    [HttpPut("{id:int}"), RequirePerm("team.manage")]
    public async Task<IActionResult> Update(int id, UserInput input)
    {
        var user = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == id) ?? throw AppException.NotFound("User");
        if (id == me.RequiredId && (input.Active == false || input.Role != user.Role.Name))
            throw AppException.Bad("CANNOT_MODIFY_SELF", "You can't change your own access", "Ask another Delivery Manager or an Admin to change your role or deactivate you.");
        if (user.Role.Name == "Admin" && me.Role != "Admin") throw AppException.Forbidden("Only an Admin can change another Admin.");

        var (roleId, teamId) = await ValidateAsync(input, id);
        var reports = await db.Users.CountAsync(u => u.ManagerUserId == id && u.IsActive);
        if (reports > 0 && (input.Role != user.Role.Name || input.Active == false))
            throw AppException.Conflict("HAS_REPORTS", "This person still manages people", $"Move their {reports} direct report{(reports == 1 ? "" : "s")} to another manager first.");

        var deactivate = user.IsActive && input.Active == false;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            user.FullName = input.Name!.Trim();
            user.Email = input.Email!.Trim().ToLowerInvariant();
            user.RoleId = roleId;
            user.ManagerUserId = input.ManagerId;
            user.TeamId = teamId;
            user.IsActive = input.Active ?? user.IsActive;
            await db.SaveChangesAsync();
            if (deactivate && user.ManagerUserId is int newOwner)
            {
                // Hand the leaver's work to their manager.
                await db.Consultants.Where(c => c.OwnerUserId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.OwnerUserId, newOwner));
                await db.Submissions.Where(x => x.OwnerUserId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerUserId, newOwner));
            }
            await tx.CommitAsync();
        }
        await RebuildHierarchyAsync();
        await audit.LogAsync(deactivate ? "USER_DEACTIVATE" : "USER_UPDATE",
            $"{(deactivate ? "Deactivated" : "Updated")} {user.FullName} ({input.Role}){(deactivate ? "; records reassigned to their manager" : "")}", "User", id);
        return NoContent();
    }

    /// <summary>Clears a lockout after too many failed sign-ins.</summary>
    [HttpPost("{id:int}/unlock"), RequirePerm("team.manage")]
    public async Task<IActionResult> Unlock(int id)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == id) ?? throw AppException.NotFound("User");
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        await db.SaveChangesAsync();
        await audit.LogAsync("USER_UNLOCK", $"Unlocked {user.FullName}", "User", id);
        return NoContent();
    }

    /// <summary>Issues a new temporary password (returned once) and unlocks the account. Only an Admin can reset an Admin.</summary>
    [HttpPost("{id:int}/reset-password"), RequirePerm("team.manage")]
    public async Task<object> ResetPassword(int id)
    {
        var user = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == id) ?? throw AppException.NotFound("User");
        if (user.Role.Name == "Admin" && me.Role != "Admin") throw AppException.Forbidden("Only an Admin can reset another Admin's password.");
        var temp = TempPassword();
        user.PasswordHash = Hasher.HashPassword(user.Email, temp);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        await db.SaveChangesAsync();
        await audit.LogAsync("PASSWORD_RESET", $"Reset password for {user.FullName}", "User", id);
        return new { temporaryPassword = temp };
    }
}

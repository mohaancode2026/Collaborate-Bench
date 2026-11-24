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

/// <summary>Team activity feed and the security audit log (both limited to the users the caller may see).</summary>
[ApiController, Route("api/activity")]
public sealed class ActivityController(DataService data, AuditService audit) : ControllerBase
{
    /// <summary>Recent successful actions by people in your view, for the dashboard feed. Sign-ins are left out.</summary>
    [HttpGet, RequirePerm("dash.view")]
    public Task<List<ActivityDto>> Feed([FromQuery] int take = 40) => data.ActivityAsync(take, "Success", false);

    /// <summary>Full audit trail including sign-ins and denied attempts. <paramref name="outcome"/> filters on Success / Denied.</summary>
    [HttpGet("audit"), RequirePerm("audit.view")]
    public Task<List<ActivityDto>> Audit([FromQuery] string? outcome, [FromQuery] int take = 200) =>
        data.ActivityAsync(take, outcome is "Denied" or "Success" or "Error" ? outcome : null, true);

    /// <summary>One page of the audit trail, newest first. <paramref name="q"/> searches user, action and detail.</summary>
    [HttpGet("audit/page"), RequirePerm("audit.view")]
    public Task<PagedResult<ActivityDto>> AuditPage([FromQuery] string? outcome, [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default) =>
        data.AuditPageAsync(outcome is "Denied" or "Success" or "Error" ? outcome : null, q, page, pageSize, ct);

    public sealed record NoteInput(string? Text);

    /// <summary>Posts a short note (max 500 characters) to the team activity feed.</summary>
    [HttpPost("note"), RequirePerm("team.view")]
    public async Task<IActionResult> Note(NoteInput input)
    {
        var text = (input.Text ?? "").Trim();
        new Validator().Check(text.Length > 0, "text", "Write a message first").Check(text.Length <= 500, "text", "Keep notes under 500 characters").ThrowIfInvalid();
        await audit.LogAsync("NOTE", "💬 " + text);
        return NoContent();
    }
}

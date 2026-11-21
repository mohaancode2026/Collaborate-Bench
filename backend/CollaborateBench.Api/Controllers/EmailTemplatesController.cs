using System.Globalization;
using System.Text.RegularExpressions;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Controllers;

/// <summary>Vendor email templates with {{merge.fields}}. Team (shared) templates are edited by Team Lead and above
/// ("templates.manage"); everyone can keep personal templates.</summary>
[ApiController, Route("api/mail/templates")]
public sealed class EmailTemplatesController(AppDbContext db, DataService data, ICurrentUser me, AuditService audit) : ControllerBase
{
    public static readonly string[] Categories = ["Submission", "Follow-up", "Rate", "Interview", "Onboarding", "Hotlist", "Other"];

    private IQueryable<EmailTemplate> Visible()
    {
        var meId = me.RequiredId;
        return db.EmailTemplates.Where(t => t.IsShared || t.OwnerUserId == meId);
    }

    private bool CanEdit(EmailTemplate t) => t.IsShared ? me.Has("templates.manage") : t.OwnerUserId == me.RequiredId;

    private EmailTemplateDto Dto(EmailTemplate t) => new(t.TemplateId, t.Name, t.Category, t.Subject, t.Body, t.IsShared, t.OwnerUserId == null,
        t.Owner?.FullName, CanEdit(t), MergeFields.Needs(t.Subject, t.Body), t.UseCount, t.LastUsedUtc, t.UpdatedUtc);

    /// <summary>Team templates plus your own, grouped by category.</summary>
    [HttpGet, RequirePerm("mail.view")]
    public async Task<List<EmailTemplateDto>> List() =>
        (await Visible().AsNoTracking().Include(t => t.Owner).ToListAsync())
            .OrderBy(t => Array.IndexOf(Categories, t.Category)).ThenByDescending(t => t.IsShared).ThenBy(t => t.Name).Select(Dto).ToList();

    /// <summary>The merge fields the editor offers, and the template categories.</summary>
    [HttpGet("fields"), RequirePerm("mail.view")]
    public object Fields() => new { fields = MergeFields.All, categories = Categories };

    /// <summary>Creates a template. Shared templates need "templates.manage". Unknown merge fields are rejected.</summary>
    [HttpPost, RequirePerm("mail.view")]
    public async Task<EmailTemplateDto> Create(EmailTemplateInput input)
    {
        if (input.Shared) me.Require("templates.manage");
        var t = new EmailTemplate { OwnerUserId = me.RequiredId };
        await ApplyAsync(t, input);
        db.EmailTemplates.Add(t);
        await db.SaveChangesAsync();
        await audit.LogAsync("TEMPLATE_CREATE", $"Created {(t.IsShared ? "shared" : "personal")} email template \"{t.Name}\"");
        t.Owner = await db.Users.FindAsync(me.RequiredId);
        return Dto(t);
    }

    /// <summary>Updates a template you may edit (your own, or a shared one with "templates.manage").</summary>
    [HttpPut("{id:int}"), RequirePerm("mail.view")]
    public async Task<EmailTemplateDto> Update(int id, EmailTemplateInput input)
    {
        var t = await Visible().Include(x => x.Owner).FirstOrDefaultAsync(x => x.TemplateId == id) ?? throw AppException.NotFound("Template");
        if (!CanEdit(t) || (input.Shared && !t.IsShared && !me.Has("templates.manage")))
            throw AppException.Forbidden(t.IsShared ? "Only Team Leads and above can change shared templates. Duplicate it to make your own version." : "You can only change your own templates.");
        if (!input.Shared && t.IsShared && t.OwnerUserId == null)
            throw AppException.Bad("BUILTIN_TEMPLATE", "Built-in template", "Built-in templates stay shared. Duplicate it to make a personal copy.");
        await ApplyAsync(t, input);
        t.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.LogAsync("TEMPLATE_UPDATE", $"Updated email template \"{t.Name}\"");
        return Dto(t);
    }

    /// <summary>Copies any template you can see into a personal template you can change.</summary>
    [HttpPost("{id:int}/duplicate"), RequirePerm("mail.view")]
    public async Task<EmailTemplateDto> Duplicate(int id)
    {
        var src = await Visible().AsNoTracking().FirstOrDefaultAsync(x => x.TemplateId == id) ?? throw AppException.NotFound("Template");
        var meId = me.RequiredId;
        var name = src.Name.StartsWith("Copy of ") ? src.Name : "Copy of " + src.Name;
        if (name.Length > 100) name = name[..100];
        for (var n = 2; await db.EmailTemplates.AnyAsync(x => x.OwnerUserId == meId && !x.IsShared && x.Name == name); n++)
            name = $"{(src.Name.Length > 80 ? src.Name[..80] : src.Name)} ({n})";
        var t = new EmailTemplate { Name = name, Category = src.Category, Subject = src.Subject, Body = src.Body, IsShared = false, OwnerUserId = meId };
        db.EmailTemplates.Add(t);
        await db.SaveChangesAsync();
        t.Owner = await db.Users.FindAsync(meId);
        return Dto(t);
    }

    /// <summary>Deletes a template you may edit.</summary>
    [HttpDelete("{id:int}"), RequirePerm("mail.view")]
    public async Task<IActionResult> Delete(int id)
    {
        var t = await Visible().FirstOrDefaultAsync(x => x.TemplateId == id) ?? throw AppException.NotFound("Template");
        if (!CanEdit(t)) throw AppException.Forbidden(t.IsShared ? "Only Team Leads and above can delete shared templates." : "You can only delete your own templates.");
        db.EmailTemplates.Remove(t);
        await db.SaveChangesAsync();
        await audit.LogAsync("TEMPLATE_DELETE", $"Deleted email template \"{t.Name}\"");
        return NoContent();
    }

    /// <summary>Fills in a saved template (or unsaved subject/body, for the editor preview). Sample = example values instead of real records.</summary>
    [HttpPost("render"), RequirePerm("mail.view")]
    public async Task<RenderResult> Render(RenderInput input)
    {
        string subject, body;
        if (input.TemplateId is int tid)
        {
            var t = await Visible().AsNoTracking().FirstOrDefaultAsync(x => x.TemplateId == tid) ?? throw AppException.NotFound("Template");
            (subject, body) = (t.Subject, t.Body);
        }
        else (subject, body) = (input.Subject ?? "", input.Body ?? "");

        if (input.Sample) return MergeFields.Render(subject, body, MergeFields.All.ToDictionary(f => f.Key, f => (string?)f.Example, StringComparer.OrdinalIgnoreCase));

        var v = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["me.name"] = me.Name, ["me.firstName"] = me.Name.Split(' ')[0], ["today"] = DateTime.UtcNow.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
            ["me.email"] = await db.Users.Where(u => u.UserId == me.RequiredId).Select(u => u.Email).FirstOrDefaultAsync(),
        };
        if (input.ConsultantId is int cid)
        {
            var c = await data.ConsultantAsync(cid);   // team rules apply
            v["consultant.name"] = c.Name; v["consultant.firstName"] = c.Name.Split(' ')[0]; v["consultant.title"] = c.Title;
            v["consultant.experience"] = c.Exp > 0 ? c.Exp.ToString(CultureInfo.InvariantCulture) : null;
            v["consultant.skills"] = string.Join(", ", c.Skills.Take(6)); v["consultant.visa"] = c.Visa; v["consultant.location"] = c.Location;
            v["consultant.relocation"] = c.Relocate ? "Open to relocate" : "Not open to relocate"; v["consultant.summary"] = c.Summary;
        }
        if (input.RequirementId is int rid)
        {
            var r = await data.RequirementAsync(rid);
            v["requirement.title"] = r.Title; v["requirement.client"] = r.Client; v["requirement.location"] = r.Location;
            v["requirement.mode"] = r.Mode; v["requirement.type"] = r.Type; v["requirement.rate"] = r.Rate > 0 ? $"${r.Rate:0}/hr" : null;
        }

        // Contact and vendor come from the first recipient: vendor by domain, name from the last email they sent the team.
        var first = (input.To ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToLowerInvariant();
        if (first != null && Validator.IsEmail(first))
        {
            var vendor = new VendorMatcher(await db.Vendors.AsNoTracking().Where(x => x.IsActive).ToListAsync()).Match(first) is int vid
                ? await db.Vendors.Where(x => x.VendorId == vid).Select(x => x.Name).FirstOrDefaultAsync() : null;
            var visible = data.VisibleUsers().Select(u => u.UserId);
            var name = await db.VendorEmails.Where(e => e.FromAddress == first && e.FromName != null && visible.Contains(e.UserId))
                .OrderByDescending(e => e.SentUtc).Select(e => e.FromName).FirstOrDefaultAsync();
            v["vendor.name"] = vendor;
            // Names taken from an address ("priya") get a capital; real display names are left as they are.
            static string? Cap(string? s) => string.IsNullOrEmpty(s) || s != s.ToLowerInvariant() ? s : char.ToUpperInvariant(s[0]) + s[1..];
            v["contact.name"] = Cap(name);
            v["contact.firstName"] = Cap(name?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
        }
        v["contact.firstName"] ??= "there";   // "Hi there," reads fine when the name isn't known
        return MergeFields.Render(subject, body, v);
    }

    private async Task ApplyAsync(EmailTemplate t, EmailTemplateInput input)
    {
        var name = Regex.Replace(input.Name ?? "", @"\s+", " ").Trim();
        var subject = (input.Subject ?? "").Trim();
        var body = (input.Body ?? "").Replace("\r\n", "\n").Trim();
        var unknown = MergeFields.Used(subject + "\n" + body).Where(k => !MergeFields.Keys.Contains(k)).Distinct().ToList();
        var meId = me.RequiredId;
        var dup = await db.EmailTemplates.AnyAsync(x => x.TemplateId != t.TemplateId && x.Name == name
            && (input.Shared ? x.IsShared : !x.IsShared && x.OwnerUserId == meId));
        new Validator()
            .Check(name.Length is >= 2 and <= 100, "name", "Give the template a name (2–100 characters)")
            .Check(!dup, "name", input.Shared ? "A shared template with this name already exists" : "You already have a template with this name")
            .Check(input.Category != null && Categories.Contains(input.Category), "category", "Choose a category")
            .Check(subject.Length > 0, "subject", "Add a subject")
            .Check(subject.Length <= 255, "subject", "Keep the subject under 255 characters")
            .Check(body.Length > 0, "body", "Write the email")
            .Check(body.Length <= 20000, "body", "Keep the email under 20,000 characters")
            .Check(unknown.Count == 0, "body", $"Unknown field{(unknown.Count == 1 ? "" : "s")}: {string.Join(", ", unknown.Select(k => "{{" + k + "}}"))}. Pick fields from the list.")
            .ThrowIfInvalid();
        t.Name = name; t.Category = input.Category!; t.Subject = subject; t.Body = body; t.IsShared = input.Shared;
    }
}

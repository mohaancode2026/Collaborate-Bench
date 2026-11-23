using CollaborateBench.Api.Ai;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Controllers;

/// <summary>
/// Backend for the Collaborate-Bench LinkedIn side panel (browser extension) and the portal's Leads page.
/// The extension sends only what is on the LinkedIn page the recruiter has open and acts only when they click;
/// there is no crawling, auto-messaging or auto-applying. Profiles become "Lead" consultants (kept out of bench lists
/// until moved to the bench); job posts become requirements. Outreach (activity, lead mapping, reminders) is visible to
/// everyone who can see the consultant, so teammates don't contact the same person twice.
/// </summary>
[ApiController, Route("api/linkedin")]
public sealed class LinkedInController(AppDbContext db, DataService data, AiService ai, ICurrentUser me, AuditService audit) : ControllerBase
{
    public static readonly string[] Channels = ["LinkedIn message", "InMail", "Connection request", "Call", "Email", "SMS", "WhatsApp", "Note"];
    public static readonly string[] Outcomes = ["Interested", "Not interested", "Call back later", "No response", "Left voicemail", "Wrong contact", "Shared resume", "Rate discussed"];
    private static readonly string[] LeadStatuses = ["Mapped", "Rejected"];
    private static readonly string[] ReminderStatuses = ["Open", "Done", "Dismissed"];

    /// <summary>Allowed channel and outcome values for the activity form.</summary>
    [HttpGet("options"), RequirePerm("bench.view")]
    public object Options() => new { channels = Channels, outcomes = Outcomes };

    // =====================================================================================================
    // Profiles
    // =====================================================================================================

    /// <summary>Looks up the profile on screen: saved record, leads, activity, reminders, suggested jobs and heads-ups. Saves nothing.</summary>
    [HttpPost("profile/lookup"), RequirePerm("bench.view")]
    public async Task<LiProfileContext> LookupProfile(LiProfileInput input, CancellationToken ct)
    {
        var url = LinkedIn.ProfileUrl(input.Url) ?? throw AppException.Bad("NOT_A_PROFILE", "Not a LinkedIn profile", "Open a LinkedIn profile page (linkedin.com/in/…) and try again.");
        var hit = await db.Consultants.IgnoreQueryFilters().AsNoTracking().Where(c => c.LinkedInUrl == url && c.Status != "Inactive")
            .Select(c => new { c.ConsultantId, Owner = c.Owner.FullName, Team = c.Owner.Team != null ? c.Owner.Team.Name : null }).FirstOrDefaultAsync(ct);
        if (hit != null && await db.Consultants.AnyAsync(c => c.ConsultantId == hit.ConsultantId, ct))
            return await ContextAsync(hit.ConsultantId, ct);

        var elsewhere = hit is null ? null : new LiOwnerHint(hit.Owner, hit.Team);
        var insights = new List<LiInsight>();
        if (elsewhere != null)
            insights.Add(new("danger", $"Already saved in Collaborate-Bench by {elsewhere.OwnerName}{(elsewhere.Team != null ? $" ({elsewhere.Team})" : "")}. Check with them before reaching out."));
        insights.AddRange(RegionInsights(input.Location));
        return new LiProfileContext(false, url, null, elsewhere, insights, [], [], [], []);
    }

    /// <summary>The side panel's view of a saved consultant (also used by the portal's Leads page).</summary>
    [HttpGet("consultants/{id:int}/context"), RequirePerm("bench.view")]
    public Task<LiProfileContext> Context(int id, CancellationToken ct) => ContextAsync(id, ct);

    /// <summary>Saves the profile on screen as a Lead, or refreshes the saved one (fills empty fields, adds new skills).
    /// The AI resume parser reads the page text for title, skills and summary; the rules engine is the fallback.
    /// 409 LINKEDIN_DUPLICATE when someone outside your team already saved this profile.</summary>
    [HttpPost("profile"), RequirePerm("bench.create"), EnableRateLimiting("ai")]
    public async Task<LiProfileContext> SaveProfile(LiProfileInput input, CancellationToken ct)
    {
        var url = LinkedIn.ProfileUrl(input.Url);
        var name = LinkedIn.Clip(input.Name, 120);
        var email = LinkedIn.Clip(input.Email, 256)?.ToLowerInvariant();
        new Validator()
            .Check(url != null, "url", "Open a LinkedIn profile page (linkedin.com/in/…)")
            .Check(name != null, "name", "The profile name couldn't be read. Scroll to the top of the profile and try again.")
            .Check(email is null || Validator.IsEmail(email), "email", "That email doesn't look valid")
            .ThrowIfInvalid();

        var pageSkills = (input.Skills ?? []).Select(s => s.Trim()).Where(s => s.Length is > 0 and <= 60).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        var text = LinkedIn.ProfileText(name!, input.Headline, input.Location, input.About, input.Experience, input.Education, pageSkills);
        var parsed = await ai.ParseResumeAsync(text, ct);

        // Same profile already saved? (by URL; or by email for a consultant added before they were linked to LinkedIn)
        var existing = await db.Consultants.IgnoreQueryFilters().Include(c => c.Owner).ThenInclude(o => o.Team)
            .FirstOrDefaultAsync(c => c.LinkedInUrl == url, ct);
        existing ??= email is null ? null : await db.Consultants.FirstOrDefaultAsync(c => c.Email == email && c.Status != "Inactive" && c.LinkedInUrl == null, ct);
        if (existing is { Status: not "Inactive" } && !await db.Consultants.AnyAsync(c => c.ConsultantId == existing.ConsultantId, ct))
            throw AppException.Conflict("LINKEDIN_DUPLICATE", "Already in Collaborate-Bench",
                $"{existing.FullName} is already saved by {existing.Owner.FullName}{(existing.Owner.Team != null ? $" ({existing.Owner.Team.Name})" : "")}. Check with them or your Team Lead before reaching out.");

        var created = existing is null;
        var reactivated = existing is { Status: "Inactive" };
        var c = existing ?? new Consultant { OwnerUserId = me.RequiredId, Status = "Lead", Source = "LinkedIn", FullName = name! };
        if (reactivated) { c.Status = "Lead"; c.OwnerUserId = me.RequiredId; }   // removed earlier: it comes back as your lead

        var title = !string.IsNullOrWhiteSpace(parsed.Title) ? parsed.Title : LinkedIn.TitleFromHeadline(input.Headline);
        c.LinkedInUrl = url;
        if (string.IsNullOrWhiteSpace(c.Title) && !string.IsNullOrWhiteSpace(title)) c.Title = LinkedIn.Clip(title, 120);
        if (string.IsNullOrWhiteSpace(c.Location)) c.Location = LinkedIn.Clip(input.Location, 120);
        if (string.IsNullOrWhiteSpace(c.Email) && email != null) c.Email = email;
        if (string.IsNullOrWhiteSpace(c.Phone)) c.Phone = LinkedIn.Clip(input.Phone, 30);
        if (string.IsNullOrWhiteSpace(c.VisaStatus) && Domain.Visas.Contains(parsed.Visa)) c.VisaStatus = parsed.Visa;
        if (string.IsNullOrWhiteSpace(c.HotlistSummary)) c.HotlistSummary = LinkedIn.Clip(parsed.Summary, 600);
        var years = parsed.Exp > 0 ? parsed.Exp : LinkedIn.YearsFromExperience(input.Experience);
        if ((c.ExperienceYears ?? 0) == 0 && years > 0) c.ExperienceYears = (byte)Math.Min(years, 60);
        c.Skills = c.Skills.Concat(parsed.Skills).Concat(pageSkills).Select(s => s.Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        c.UpdatedUtc = DateTime.UtcNow;
        if (created) db.Consultants.Add(c);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (SqlErrors.IsDuplicate(e))
        {
            throw AppException.Conflict("LINKEDIN_DUPLICATE", "Already in Collaborate-Bench",
                "This profile (or its email) was just saved by someone else. Refresh the panel to see it.");
        }

        // Keep the page text as the consultant's "resume" so search and the AI tools can use it; replace the previous LinkedIn copy.
        var copy = await db.Resumes.FirstOrDefaultAsync(r => r.ConsultantId == c.ConsultantId && r.FileName == "LinkedIn profile", ct);
        if (copy is null) db.Resumes.Add(copy = new Resume { ConsultantId = c.ConsultantId, FileName = "LinkedIn profile" });
        copy.ExtractedText = text;
        copy.ParsedBy = parsed.Source == "claude" ? "claude" : "rules";
        copy.UploadedByUserId = me.RequiredId;
        copy.UploadedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(created ? "LINKEDIN_CAPTURE" : "LINKEDIN_REFRESH",
            $"{(created ? "Saved" : reactivated ? "Re-added" : "Refreshed")} {c.FullName} from LinkedIn", "Consultant", c.ConsultantId);
        var ctx = await ContextAsync(c.ConsultantId, ct);
        return ctx with { Engine = parsed.Source, Notice = parsed.Notice };
    }

    // =====================================================================================================
    // Leads (candidate ↔ requirement), activity, reminders
    // =====================================================================================================

    /// <summary>Maps the candidate to a requirement as a lead, or marks them rejected for it (updates the existing link).</summary>
    [HttpPost("leads"), RequirePerm("bench.edit")]
    public async Task<LiProfileContext> SetLead(LeadInput input, CancellationToken ct)
    {
        var status = string.IsNullOrEmpty(input.Status) ? "Mapped" : input.Status;
        new Validator()
            .Check(LeadStatuses.Contains(status), "status", "Status must be Mapped or Rejected")
            .Check((input.Reason?.Length ?? 0) <= 300, "reason", "Keep the reason under 300 characters")
            .ThrowIfInvalid();
        var c = await data.ConsultantAsync(input.ConsultantId, ct);
        var r = await data.RequirementAsync(input.RequirementId, ct);

        var lead = await db.CandidateLeads.FirstOrDefaultAsync(l => l.ConsultantId == c.Id && l.RequirementId == r.Id, ct);
        if (lead is null) db.CandidateLeads.Add(lead = new CandidateLead { ConsultantId = c.Id, RequirementId = r.Id });
        lead.Status = status;
        lead.Reason = LinkedIn.Clip(input.Reason, 300);
        lead.UpdatedByUserId = me.RequiredId;
        lead.UpdatedUtc = DateTime.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (SqlErrors.IsDuplicate(e)) { }   // a teammate mapped it at the same moment: theirs stands

        await audit.LogAsync("LEAD_" + status.ToUpperInvariant(), $"{(status == "Mapped" ? "Mapped" : "Rejected")} {c.Name} as a lead for \"{r.Title}\"", "Consultant", c.Id);
        return await ContextAsync(c.Id, ct);
    }

    /// <summary>Removes the lead link between a candidate and a requirement.</summary>
    [HttpDelete("leads/{leadId:int}"), RequirePerm("bench.edit")]
    public async Task<LiProfileContext> RemoveLead(int leadId, CancellationToken ct)
    {
        var lead = await db.CandidateLeads.FirstOrDefaultAsync(l => l.LeadId == leadId, ct) ?? throw AppException.NotFound("Lead");
        db.CandidateLeads.Remove(lead);
        await db.SaveChangesAsync(ct);
        return await ContextAsync(lead.ConsultantId, ct);
    }

    /// <summary>Logs an outreach touch (message, call, email…) with its outcome, optionally setting a reminder in the same step.
    /// "Not interested" on a requirement also marks the candidate rejected for it.</summary>
    [HttpPost("activities"), RequirePerm("bench.edit")]
    public async Task<LiProfileContext> LogActivity(CandidateActivityInput input, CancellationToken ct)
    {
        var channel = string.IsNullOrEmpty(input.Channel) ? "Note" : input.Channel;
        new Validator()
            .Check(Channels.Contains(channel), "channel", "Choose how you contacted them")
            .Check(string.IsNullOrEmpty(input.Outcome) || Outcomes.Contains(input.Outcome), "outcome", "Choose a valid outcome")
            .Check(!string.IsNullOrWhiteSpace(input.Outcome) || !string.IsNullOrWhiteSpace(input.Note), "note", "Add an outcome or a note")
            .Check((input.Note?.Length ?? 0) <= 1000, "note", "Keep the note under 1,000 characters")
            .Check(input.RemindUtc is null || input.RemindUtc > DateTime.UtcNow.AddMinutes(-5), "remindUtc", "Pick a reminder time in the future")
            .ThrowIfInvalid();
        var c = await data.ConsultantAsync(input.ConsultantId, ct);
        RequirementDto? r = input.RequirementId is int rid ? await data.RequirementAsync(rid, ct) : null;

        db.CandidateActivities.Add(new CandidateActivity
        {
            ConsultantId = c.Id, RequirementId = r?.Id, UserId = me.RequiredId, Channel = channel,
            Outcome = string.IsNullOrEmpty(input.Outcome) ? null : input.Outcome, Note = LinkedIn.Clip(input.Note, 1000),
        });
        if (input.RemindUtc is DateTime due)
            db.CandidateReminders.Add(new CandidateReminder
            {
                ConsultantId = c.Id, UserId = me.RequiredId, DueUtc = due.ToUniversalTime(),
                Note = LinkedIn.Clip(input.Note, 300) ?? (input.Outcome == "Call back later" ? "Call back" : $"Follow up ({channel})"),
            });
        if (r != null && input.Outcome == "Not interested")
        {
            var lead = await db.CandidateLeads.FirstOrDefaultAsync(l => l.ConsultantId == c.Id && l.RequirementId == r.Id, ct);
            if (lead is null) db.CandidateLeads.Add(lead = new CandidateLead { ConsultantId = c.Id, RequirementId = r.Id });
            lead.Status = "Rejected"; lead.Reason = "Not interested"; lead.UpdatedByUserId = me.RequiredId; lead.UpdatedUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return await ContextAsync(c.Id, ct);
    }

    /// <summary>Candidate reminders, soonest first. <paramref name="scope"/>: mine (default) or team.
    /// <paramref name="status"/>: open (default) or closed (last 30 days).</summary>
    [HttpGet("reminders"), RequirePerm("bench.view")]
    public async Task<List<CandidateReminderDto>> Reminders([FromQuery] string? status, [FromQuery] string? scope, CancellationToken ct)
    {
        var meId = me.RequiredId;
        var q = db.CandidateReminders.AsNoTracking().AsQueryable();
        if (scope is "team" && me.DataScope != "OWN") { var visible = data.VisibleUsers().Select(u => u.UserId); q = q.Where(x => visible.Contains(x.UserId)); }
        else q = q.Where(x => x.UserId == meId);
        var since = DateTime.UtcNow.AddDays(-30);
        q = status == "closed" ? q.Where(x => x.Status != "Open" && x.ClosedUtc >= since) : q.Where(x => x.Status == "Open");
        return (await ProjectReminders(q.OrderBy(x => x.DueUtc).Take(200)).ToListAsync(ct)).Select(Finish).ToList();
    }

    /// <summary>Sets a reminder to get back to a candidate.</summary>
    [HttpPost("reminders"), RequirePerm("bench.edit")]
    public async Task<CandidateReminderDto> AddReminder(CandidateReminderInput input, CancellationToken ct)
    {
        new Validator()
            .Check(input.DueUtc > DateTime.UtcNow.AddMinutes(-5), "dueUtc", "Pick a time in the future")
            .Check((input.Note?.Length ?? 0) <= 300, "note", "Keep the note under 300 characters")
            .ThrowIfInvalid();
        var c = await data.ConsultantAsync(input.ConsultantId, ct);
        var r = new CandidateReminder { ConsultantId = c.Id, UserId = me.RequiredId, DueUtc = input.DueUtc.ToUniversalTime(), Note = LinkedIn.Clip(input.Note, 300) };
        db.CandidateReminders.Add(r);
        await db.SaveChangesAsync(ct);
        return Finish(await ProjectReminders(db.CandidateReminders.AsNoTracking().Where(x => x.ReminderId == r.ReminderId)).FirstAsync(ct));
    }

    /// <summary>Closes (Done/Dismissed) or reopens one of your reminders.</summary>
    [HttpPatch("reminders/{id:int}"), RequirePerm("bench.edit")]
    public async Task<CandidateReminderDto> SetReminderStatus(int id, CandidateReminderStatusInput input, CancellationToken ct)
    {
        if (!ReminderStatuses.Contains(input.Status)) throw AppException.Bad("VALIDATION", "Invalid status", "Status must be Open, Done or Dismissed.");
        var r = await db.CandidateReminders.FirstOrDefaultAsync(x => x.ReminderId == id && x.UserId == me.RequiredId, ct) ?? throw AppException.NotFound("Reminder");
        r.Status = input.Status!;
        r.ClosedUtc = input.Status == "Open" ? null : DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Finish(await ProjectReminders(db.CandidateReminders.AsNoTracking().Where(x => x.ReminderId == id)).FirstAsync(ct));
    }

    /// <summary>Sourced leads (not yet on the bench) for the portal's Leads page, newest first, with their latest touch and next reminder.</summary>
    [HttpGet("leads"), RequirePerm("bench.view")]
    public async Task<List<LeadRow>> Leads([FromQuery] string? scope, CancellationToken ct)
    {
        var leads = await data.ConsultantsAsync(scope, ct, leadsOnly: true);
        var ids = leads.Select(c => c.Id).ToList();
        var acts = await db.CandidateActivities.AsNoTracking().Where(a => ids.Contains(a.ConsultantId)).OrderByDescending(a => a.CreatedUtc)
            .Select(a => new { a.ConsultantId, a.Channel, a.Outcome, a.CreatedUtc, By = a.User.FullName }).ToListAsync(ct);
        var due = await db.CandidateReminders.AsNoTracking().Where(r => ids.Contains(r.ConsultantId) && r.Status == "Open")
            .GroupBy(r => r.ConsultantId).Select(g => new { Id = g.Key, Due = g.Min(r => r.DueUtc) }).ToDictionaryAsync(x => x.Id, x => x.Due, ct);
        var links = await db.CandidateLeads.AsNoTracking().Where(l => ids.Contains(l.ConsultantId))
            .GroupBy(l => l.ConsultantId).Select(g => new { Id = g.Key, Mapped = g.Count(l => l.Status == "Mapped"), Rejected = g.Count(l => l.Status == "Rejected") })
            .ToDictionaryAsync(x => x.Id, ct);
        var last = acts.GroupBy(a => a.ConsultantId).ToDictionary(g => g.Key, g => g.First());
        return leads.Select(c =>
        {
            var a = last.GetValueOrDefault(c.Id);
            var l = links.GetValueOrDefault(c.Id);
            return new LeadRow(c, a is null ? null : a.Outcome is null ? a.Channel : $"{a.Channel}: {a.Outcome}", a?.CreatedUtc, a?.By,
                due.TryGetValue(c.Id, out var d) ? d : null, l?.Mapped ?? 0, l?.Rejected ?? 0);
        }).ToList();
    }

    // =====================================================================================================
    // Job posts
    // =====================================================================================================

    /// <summary>Looks up the job post on screen. Saved already: the requirement and its bench matches. Otherwise: an AI-extracted
    /// draft to review (title, client/vendor, mode, type, rate, skills, visas) and the bench consultants it would match. Saves nothing.</summary>
    [HttpPost("job/lookup"), RequirePerm("jobs.view"), EnableRateLimiting("ai")]
    public async Task<LiJobContext> LookupJob(LiJobInput input, CancellationToken ct)
    {
        var url = LinkedIn.JobUrl(input.Url) ?? throw AppException.Bad("NOT_A_JOB", "Not a LinkedIn job", "Open a LinkedIn job post (linkedin.com/jobs/…) and try again.");
        var savedId = await db.Requirements.AsNoTracking().Where(r => r.SourceUrl == url).Select(r => (int?)r.RequirementId).FirstOrDefaultAsync(ct);
        if (savedId is int id)
        {
            var r = await data.RequirementAsync(id, ct);
            var insights = new List<LiInsight> { new("info", $"Saved as a requirement by {r.PostedByName} {Ago(r.PostedUtc)}{(r.Status == "Closed" ? " (now closed)" : "")}.") };
            if (r.ActiveSubmissions > 0) insights.Add(new("info", $"{r.ActiveSubmissions} active submission(s) on this job."));
            return new LiJobContext(true, url, r, null, null, null, await MatchesAsync(r, ct), insights);
        }

        if (string.IsNullOrWhiteSpace(input.Title) && string.IsNullOrWhiteSpace(input.Description))
            throw AppException.Bad("EMPTY_PAGE", "Couldn't read the job", "The job details didn't load yet. Wait for the page to finish loading and try again.");
        var text = string.Join("\n", new[] { input.Title, input.Company, input.Location, input.Workplace, input.EmploymentType, "", input.Description }
            .Select(s => s ?? "")).Trim();
        var ext = await ai.ExtractRequirementAsync(text.Length > 9000 ? text[..9000] : text, ct);
        var draft = await DraftAsync(ext.Requirement, input, ct);

        var preview = new RequirementDto
        {
            Title = draft.Title ?? "", Client = draft.Client ?? "", Vendor = draft.Vendor ?? "", Location = draft.Location ?? "",
            Mode = draft.Mode ?? "Hybrid", Type = draft.Type ?? "C2C", Rate = draft.Rate, Skills = draft.Skills ?? [], Visas = draft.Visas ?? ["Any"],
        };
        var notes = new List<LiInsight>();
        if (draft.Rate <= 0) notes.Add(new("info", "No bill rate on the post. Confirm the rate with the vendor before submitting."));
        if (draft.Visas is { Count: > 0 } v && !v.Contains("Any")) notes.Add(new("warn", $"Visa restriction: {string.Join(", ", v)} only."));
        if ((draft.Skills?.Count ?? 0) == 0) notes.Add(new("warn", "No skills found. Add the key skills before saving so matching works."));
        return new LiJobContext(false, url, null, draft, ext.Source, ext.Notice, await MatchesAsync(preview, ct), notes);
    }

    /// <summary>Saves the reviewed job post as an open requirement (Source "LinkedIn") and returns it with bench matches.
    /// 409 DUPLICATE_REQUIREMENT when this post was already saved.</summary>
    [HttpPost("job"), RequirePerm("jobs.create")]
    public async Task<LiJobContext> SaveJob(LiJobSaveInput input, CancellationToken ct)
    {
        var url = LinkedIn.JobUrl(input.Url);
        var j = input.Requirement ?? new RequirementInput(null, null, null, null, null, null, null, 0, null, null, null, null, null);
        var skills = (j.Skills ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        new Validator()
            .Check(url != null, "url", "Open a LinkedIn job post (linkedin.com/jobs/…)")
            .Check(!string.IsNullOrWhiteSpace(j.Title), "title", "Job title is required")
            .Check(!string.IsNullOrWhiteSpace(j.Client), "client", "End client is required (use \"Confidential\" if the post doesn't say)")
            .Check(j.Rate == 0 || j.Rate is >= 10 and <= 400, "rate", "Leave the bill rate at 0 if unknown, or enter $10–$400/hr")
            .Check(skills.Count > 0, "skills", "Add at least one required skill")
            .Check(Domain.WorkModes.Contains(j.Mode), "mode", "Choose Remote, Hybrid or Onsite")
            .Check(Domain.ContractTypes.Contains(j.Type), "type", "Choose C2C, W2 or 1099")
            .Check(string.IsNullOrEmpty(j.Tier) || Domain.Tiers.Contains(j.Tier), "tier", "Choose a valid vendor tier")
            .ThrowIfInvalid();

        var dupe = await db.Requirements.AsNoTracking().Where(r => r.SourceUrl == url).Select(r => r.PostedBy.FullName).FirstOrDefaultAsync(ct);
        if (dupe != null) throw AppException.Conflict("DUPLICATE_REQUIREMENT", "Already saved", $"{dupe} already saved this LinkedIn job. Refresh the panel to see it.");

        var req = new Requirement
        {
            Title = LinkedIn.Clip(j.Title, 200)!, EndClient = LinkedIn.Clip(j.Client, 200)!, Vendor = LinkedIn.Clip(j.Vendor, 200) ?? "Direct client",
            Tier = string.IsNullOrEmpty(j.Tier) ? "Direct" : j.Tier, Location = LinkedIn.Clip(j.Location, 200) ?? "Remote",
            WorkMode = j.Mode!, ContractType = j.Type!, BillRate = j.Rate, Skills = skills,
            AcceptedVisas = j.Visas is { Count: > 0 } v ? v.Where(x => x == "Any" || Domain.Visas.Contains(x)).DefaultIfEmpty("Any").ToList() : ["Any"],
            Description = LinkedIn.Clip(j.Description, 8000), Source = "LinkedIn", SourceUrl = url, PostedByUserId = me.RequiredId,
        };
        db.Requirements.Add(req);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (SqlErrors.IsDuplicate(e))
        {
            throw AppException.Conflict("DUPLICATE_REQUIREMENT", "Already saved", "Someone just saved this LinkedIn job. Refresh the panel to see it.");
        }
        await audit.LogAsync("REQ_CREATE", $"Saved \"{req.Title}\" for {req.EndClient} from LinkedIn", "Requirement", req.RequirementId);
        var saved = await data.RequirementAsync(req.RequirementId, ct);
        return new LiJobContext(true, url, saved, null, null, null, await MatchesAsync(saved, ct),
            [new("info", "Saved as an open requirement. It now shows on the Jobs page, in the Match Agent and for the Autopilot.")]);
    }

    // =====================================================================================================
    // helpers
    // =====================================================================================================

    /// <summary>Fills the AI draft from what the page states (title, location, workplace, employment type) and decides
    /// whether the poster is a known vendor (then the end client is unknown) or the client itself.</summary>
    private async Task<RequirementInput> DraftAsync(RequirementInput x, LiJobInput page, CancellationToken ct)
    {
        var company = LinkedIn.Clip(page.Company, 200);
        var vendorNames = await db.Vendors.AsNoTracking().Where(v => v.IsActive).Select(v => v.Name).ToListAsync(ct);
        var posterIsVendor = company != null && vendorNames.Any(n => n.Equals(company, StringComparison.OrdinalIgnoreCase));
        var mode = (page.Workplace ?? "").ToLowerInvariant() switch
        {
            var w when w.Contains("remote") => "Remote",
            var w when w.Contains("hybrid") => "Hybrid",
            var w when w.Contains("on-site") || w.Contains("onsite") => "Onsite",
            _ => Domain.WorkModes.Contains(x.Mode) ? x.Mode : "Hybrid",
        };
        // LinkedIn says "Full-time" or "Contract"; C2C vs W2 only comes from the description, so trust the AI there and default to C2C.
        var type = Domain.ContractTypes.Contains(x.Type) ? x.Type! : "C2C";
        string? client = posterIsVendor ? (string.IsNullOrWhiteSpace(x.Client) || x.Client.Equals(company, StringComparison.OrdinalIgnoreCase) ? "Confidential" : x.Client)
                                        : company ?? x.Client;
        return x with
        {
            Title = LinkedIn.Clip(page.Title, 200) ?? x.Title, Client = client, Vendor = posterIsVendor ? company : (string.IsNullOrWhiteSpace(x.Vendor) ? "Direct client" : x.Vendor),
            Tier = posterIsVendor ? (Domain.Tiers.Contains(x.Tier) && x.Tier != "Direct" ? x.Tier : "Tier-1") : "Direct",
            Location = LinkedIn.Clip(page.Location, 200) ?? x.Location, Mode = mode, Type = type,
            Rate = x.Rate is >= 10 and <= 400 ? x.Rate : 0, Visas = x.Visas is { Count: > 0 } ? x.Visas : ["Any"],
            Description = LinkedIn.Clip(page.Description, 8000), Source = "LinkedIn", Skills = (x.Skills ?? []).Take(20).ToList(),
        };
    }

    /// <summary>Available bench consultants (Bench/Marketing) scored for a requirement, best 10.</summary>
    private async Task<List<MatchResult>> MatchesAsync(RequirementDto r, CancellationToken ct)
    {
        var subs = r.Id == 0 ? new Dictionary<int, string>()
            : await db.Submissions.AsNoTracking().Where(s => s.RequirementId == r.Id && s.Stage != "Withdrawn").ToDictionaryAsync(s => s.ConsultantId, s => s.Stage, ct);
        return (await data.ConsultantsAsync(null, ct)).Where(c => c.Status is "Bench" or "Marketing")
            .Select(c => Matching.Score(c, r, subs.GetValueOrDefault(c.Id))).Where(m => m.Score >= 40)
            .OrderByDescending(m => m.Score).Take(10).ToList();
    }

    private IQueryable<CandidateReminderDto> ProjectReminders(IQueryable<CandidateReminder> q)
    {
        var meId = me.RequiredId;
        return q.Select(x => new CandidateReminderDto(x.ReminderId, x.ConsultantId, x.Consultant.FullName, x.Consultant.LinkedInUrl, x.DueUtc, x.Note,
            x.Status, false, x.User.FullName, x.UserId == meId));
    }

    private static CandidateReminderDto Finish(CandidateReminderDto r) => r with { Overdue = r.Status == "Open" && r.DueUtc < DateTime.UtcNow };

    private static IEnumerable<LiInsight> RegionInsights(string? location) => LinkedIn.Region(location) switch
    {
        "EU/EEA" => [new("warn", "The candidate appears to live in the EU/EEA. Under GDPR, get their consent before you store or share their details, and tell them how you'll use them.")],
        "UK" => [new("warn", "The candidate appears to live in the UK. Under UK GDPR, get their consent before you store or share their details.")],
        "Other" => [new("info", $"Based outside the US ({location}). Check US work authorization and relocation before submitting.")],
        _ => [],
    };

    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalMinutes < 2 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
             : d.TotalDays < 2 ? "yesterday" : $"{(int)d.TotalDays} days ago";
    }

    /// <summary>Builds the panel view: consultant, links to requirements (leads + submissions), timeline, reminders, suggestions and heads-ups.</summary>
    private async Task<LiProfileContext> ContextAsync(int consultantId, CancellationToken ct)
    {
        var c = await data.ConsultantAsync(consultantId, ct);
        var meId = me.RequiredId;

        var leads = await db.CandidateLeads.AsNoTracking().Where(l => l.ConsultantId == c.Id)
            .Select(l => new { l.LeadId, l.RequirementId, l.Requirement.Title, l.Requirement.EndClient, l.Status, l.Reason, By = l.UpdatedBy.FullName, l.UpdatedUtc, l.CreatedUtc })
            .ToListAsync(ct);
        var subs = await db.Submissions.AsNoTracking().Where(s => s.ConsultantId == c.Id && s.Stage != "Withdrawn")
            .Select(s => new { s.SubmissionId, s.RequirementId, s.Requirement.Title, s.Requirement.EndClient, s.Stage, By = s.Owner.FullName, s.CreatedUtc, s.StageChangedUtc })
            .ToListAsync(ct);
        var acts = await db.CandidateActivities.AsNoTracking().Where(a => a.ConsultantId == c.Id).OrderByDescending(a => a.CreatedUtc).Take(100)
            .Select(a => new { a.ActivityId, a.RequirementId, Title = a.Requirement != null ? a.Requirement.Title : null, a.Channel, a.Outcome, a.Note, By = a.User.FullName, a.UserId, a.CreatedUtc })
            .ToListAsync(ct);
        var reminders = (await ProjectReminders(db.CandidateReminders.AsNoTracking().Where(r => r.ConsultantId == c.Id && r.Status == "Open").OrderBy(r => r.DueUtc))
            .ToListAsync(ct)).Select(Finish).ToList();

        string Touch(string channel, string? outcome) => outcome is null ? channel : $"{channel}: {outcome}";
        var links = new List<LeadDto>();
        foreach (var s in subs)
        {
            var on = acts.Where(a => a.RequirementId == s.RequirementId).ToList();
            links.Add(new LeadDto(leads.FirstOrDefault(l => l.RequirementId == s.RequirementId)?.LeadId, s.RequirementId, s.Title, s.EndClient, "Submitted", null,
                s.Stage, s.SubmissionId, s.By, s.StageChangedUtc, on.FirstOrDefault() is { } f ? Touch(f.Channel, f.Outcome) : null, on.FirstOrDefault()?.CreatedUtc, on.Count));
        }
        foreach (var l in leads.Where(l => subs.All(s => s.RequirementId != l.RequirementId)))
        {
            var on = acts.Where(a => a.RequirementId == l.RequirementId).ToList();
            links.Add(new LeadDto(l.LeadId, l.RequirementId, l.Title, l.EndClient, l.Status, l.Reason, null, null, l.By, l.UpdatedUtc,
                on.FirstOrDefault() is { } f ? Touch(f.Channel, f.Outcome) : null, on.FirstOrDefault()?.CreatedUtc, on.Count));
        }
        links = links.OrderByDescending(x => x.LastActivityUtc ?? x.UpdatedUtc).ToList();

        var timeline = acts.Select(a => new CandidateActivityDto(a.ActivityId, "activity", a.RequirementId, a.Title, a.Channel, a.Outcome, a.Note, a.By, a.CreatedUtc))
            .Concat(leads.Select(l => new CandidateActivityDto(l.LeadId, "lead", l.RequirementId, l.Title, l.Status == "Mapped" ? "Mapped as lead" : "Rejected as lead", null, l.Reason, l.By, l.UpdatedUtc)))
            .Concat(subs.Select(s => new CandidateActivityDto(s.SubmissionId, "submission", s.RequirementId, s.Title, "Submitted", s.Stage, null, s.By, s.CreatedUtc)))
            .OrderByDescending(x => x.CreatedUtc).Take(100).ToList();

        // Suggestions: open requirements this candidate fits, leaving out ones they're already linked to.
        var linked = links.Select(x => x.RequirementId).ToHashSet();
        var suggestions = (await data.RequirementsAsync(true, ct)).Where(r => !linked.Contains(r.Id))
            .Select(r => Matching.Score(c, r)).Where(m => m.Score >= 40).OrderByDescending(m => m.Score).Take(8).ToList();

        var insights = new List<LiInsight>();
        if (c.Status != "Lead") insights.Add(new("info", $"On your {(c.Status == "Placed" ? "placed list" : "bench")} ({c.Status}), owned by {c.OwnerName}."));
        else insights.Add(new("info", $"Saved as a lead by {c.OwnerName} {Ago(c.CreatedUtc)}."));
        insights.AddRange(RegionInsights(c.Location));
        var lastOther = acts.FirstOrDefault(a => a.UserId != meId);
        if (lastOther != null && lastOther.CreatedUtc > DateTime.UtcNow.AddDays(-7))
            insights.Add(new("warn", $"{lastOther.By} contacted them {Ago(lastOther.CreatedUtc)} ({Touch(lastOther.Channel, lastOther.Outcome)}). Coordinate before reaching out."));
        var notInterested = acts.FirstOrDefault(a => a.Outcome == "Not interested");
        if (notInterested != null && notInterested.CreatedUtc > DateTime.UtcNow.AddDays(-30))
            insights.Add(new("warn", $"Said not interested {Ago(notInterested.CreatedUtc)}{(notInterested.Title != null ? $" (for {notInterested.Title})" : "")}."));
        if (reminders.FirstOrDefault(r => r.Overdue) is { } late)
            insights.Add(new("warn", $"Reminder overdue since {late.DueUtc:MMM d}: {late.Note ?? "follow up"}."));
        if (c.ActiveSubmissions > 0) insights.Add(new("info", $"{c.ActiveSubmissions} active submission(s)."));
        if (string.IsNullOrEmpty(c.Email) && string.IsNullOrEmpty(c.Phone)) insights.Add(new("info", "No email or phone on file yet. Add them before you submit."));
        if (string.IsNullOrEmpty(c.Visa) && c.Status == "Lead") insights.Add(new("info", "Visa status not known yet. Ask before mapping to visa-restricted jobs."));

        return new LiProfileContext(true, c.LinkedInUrl, c, null, insights, links, timeline, reminders, suggestions);
    }
}

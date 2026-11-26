// Request/response types for the AI Inbox (InboxController).
using CollaborateBench.Api.Services;

namespace CollaborateBench.Api.Models;

public sealed record InboxItemDto(int Id, DateTime ReceivedUtc, string FromAddress, string? FromName, string Subject, string Summary,
    string Category, string Priority, string? PriorityReason, bool NeedsReply, string Status, string Engine, bool HasAttachments,
    InboxJob? Job, List<InboxMatch> Matches, int OpenTasks, int? RequirementId,
    /// <summary>In = received, Out = sent by you.</summary>
    string Direction = "In", string? ToAddresses = null,
    /// <summary>Sent mail: when the other side replied in the thread. Received mail: when you answered. Null = not yet.</summary>
    DateTime? AnsweredUtc = null,
    List<InboxAttachmentDto>? Attachments = null,
    /// <summary>The consultant the email is about.</summary>
    int? ConsultantId = null, string? ConsultantName = null,
    /// <summary>What the vendor asks us to send; the team a question is for; interview slots offered (UTC).</summary>
    List<string>? Requests = null, string? Team = null, List<DateTime>? Slots = null);
/// <param name="Index">Download with GET inbox/{id}/attachments/{index}.</param>
public sealed record InboxAttachmentDto(int Index, string Name, long Size, string? ContentType);

public sealed record InboxTaskDto(int Id, int? InboxItemId, string? EmailSubject, string Kind, string Title, DateTime? DueUtc, int? DurationMinutes,
    int? ConsultantId, string? ConsultantName, string? ConsultantEmail, string? Location, string? Notes, DateTime? SharedUtc, string Status, bool Auto,
    /// <summary>What "Open draft" writes (Reply, Nudge, Feedback, AskConsultant, Slots, Forward:Team, Mock).</summary>
    string? Action = null, string? InterviewType = null, bool MockSession = false, List<DateTime>? Slots = null, int? ParentTaskId = null,
    int Sequence = 1, string? OwnerName = null);

public sealed record InboxDetailDto(InboxItemDto Item, string Body, List<InboxTaskDto> Tasks);

/// <param name="High">Open high-priority emails (the tile turns red when above 0).</param>
/// <param name="LatestHighId">Newest open high-priority email, so the browser can alert once per new one.</param>
public sealed record InboxSummaryDto(bool Configured, bool Connected, DateTime? LastSyncUtc, int Open, int High, int Jobs, int NeedsReply,
    int Interviews, int TasksDue, int? LatestHighId, string? LatestHighSubject, string? LatestHighFrom, DateTime? LatestHighUtc,
    /// <summary>Emails you sent 1–14 days ago that have no reply yet.</summary>
    int AwaitingReply = 0);

public sealed record InboxUpdateInput(string? Status, int? RequirementId);
/// <param name="ReplyToInboxItemId">Set when the draft replies inside an email thread; null for a new email (e.g. to the consultant).</param>
public sealed record InboxDraftDto(string To, string Subject, string Body, int? ReplyToInboxItemId, string Engine, string? Notice);
public sealed record InboxDraftInput(int? ConsultantId, string? Instructions);
public sealed record InboxTaskInput(int? InboxItemId, string? Kind, string? Title, DateTime? DueUtc, int? DurationMinutes, int? ConsultantId,
    string? Location, string? Notes, string? Status, string? InterviewType = null, bool? MockSession = null);
/// <summary>The executive's home: their open tasks with due times, and upcoming interviews (their own, plus their team's for leads).</summary>
public sealed record InboxHomeDto(List<InboxTaskDto> Tasks, List<InboxTaskDto> Interviews, int Overdue);
public sealed record InboxRematchResult(int Jobs, int WithMatches, int Consultants);
public sealed record InboxShareInput(string? Message);
public sealed record InboxSyncResult(int Read, string Status, string? Error, int NewItems);

/// <summary>An earlier send of the same consultant to the same vendor or client (shown as a warning before pitching).</summary>
public sealed record AlreadySentDto(DateTime WhenUtc, string Kind, string Text, string? By);

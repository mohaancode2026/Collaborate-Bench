// Request/response types for follow-up reminders (FollowUpsController).
namespace CollaborateBench.Api.Models;

public sealed record ReminderDto(int Id, string Status, DateTime DueUtc, bool Overdue, string? Note, bool Auto, DateTime CreatedUtc, DateTime? ClosedUtc,
    int EmailId, string Subject, string Preview, string Vendor, int VendorId, string ToAddresses, DateTime SentUtc, int DaysWaiting,
    int? ConsultantId, string? ConsultantName, int UserId, string UserName, bool Mine);
public sealed record ReminderInput(int EmailId, DateTime DueUtc, string? Note);
public sealed record ReminderUpdate(DateTime? DueUtc, string? Note, string? Status);
public sealed record ReminderCount(int Due, int Open);

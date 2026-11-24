// Request/response types for vendors, vendor email activity and sending (VendorMailController).
namespace CollaborateBench.Api.Models;

public sealed record VendorDto(int Id, string Name, List<string> Domains, bool Active, int Sent, int Received, DateTime? LastContactUtc, int AwaitingReply);
public sealed record VendorInput(string? Name, List<string>? Domains, bool? Active);
public sealed record VendorBatchInput(List<VendorInput>? Vendors);
/// <param name="Signature">What's added to emails (yours, or the default). <paramref name="Default"/> is the built-in one.</param>
/// <param name="Source">custom | outlook | default.</param>
/// <param name="Outlook">The signature read from your Outlook sent mail, if found.</param>
/// <param name="HtmlPreview">The Outlook signature as it will appear (HTML, images inlined), for a preview; null when only text is known.</param>
public sealed record MailSignatureDto(string Signature, bool Custom, string Default, string Source = "default", string? Outlook = null, string? HtmlPreview = null);
public sealed record MailSignatureInput(string? Signature);
public sealed record VendorBatchError(string Name, string Domains, string Error);
public sealed record VendorBatchResult(List<VendorDto> Created, List<VendorBatchError> Failed);
public sealed record VendorEmailDto(int Id, DateTime SentUtc, string Direction, int VendorId, string Vendor, string FromAddress, string? FromName,
    string ToAddresses, string Subject, string Preview, List<string> Attachments, int? ConsultantId, string? ConsultantName,
    int UserId, string UserName, bool AwaitingReply);
public sealed record RecruiterMailStat(int UserId, string Name, int Sent, int Received, int Vendors, int AwaitingReply, DateTime? LastContactUtc, string? Mailbox, string? MailStatus);
public sealed record MailSummary(int Days, int Sent, int Received, int VendorsContacted, int AwaitingReply, List<VendorDto> ByVendor, List<RecruiterMailStat> ByRecruiter);
public sealed record MailConnectionDto(bool Configured, bool Connected, string? Mailbox, string? Status, DateTime? LastSyncUtc, string? LastError, int Vendors, bool CanSend);
public sealed record VendorContact(string Address, string? Name, int VendorId, string Vendor, DateTime? LastContactUtc);
public sealed class SendMailForm
{
    public string? To { get; set; }
    public string? Cc { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
    /// <summary>Reply inside the thread of this tracked email (must be in your own mailbox).</summary>
    public int? ReplyToEmailId { get; set; }
    /// <summary>Reply inside the thread of this AI Inbox email (must be your own).</summary>
    public int? ReplyToInboxItemId { get; set; }
    /// <summary>Template the email started from (counts towards its usage).</summary>
    public int? TemplateId { get; set; }
    public List<IFormFile>? Files { get; set; }
}

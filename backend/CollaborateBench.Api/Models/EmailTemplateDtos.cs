// Request/response types for email templates (EmailTemplatesController).
namespace CollaborateBench.Api.Models;

public sealed record EmailTemplateDto(int Id, string Name, string Category, string Subject, string Body, bool Shared, bool BuiltIn,
    string? OwnerName, bool CanEdit, List<string> Needs, int UseCount, DateTime? LastUsedUtc, DateTime UpdatedUtc);
public sealed record EmailTemplateInput(string? Name, string? Category, string? Subject, string? Body, bool Shared);
public sealed record RenderInput(int? TemplateId, string? Subject, string? Body, int? ConsultantId, int? RequirementId, string? To, bool Sample = false);
public sealed record RenderResult(string Subject, string Body, List<string> Missing, List<string> Unknown);
public sealed record MergeField(string Key, string Label, string Group, string Example);

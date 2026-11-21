using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CollaborateBench.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IHttpContextAccessor? http = null) : DbContext(options)
{
    // Signed-in user, read per query by the team filters below. No user (e.g. login, background work) → no consultants/submissions.
    private ClaimsPrincipal? Principal => http?.HttpContext?.User;
    public int CurrentUserId => int.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
    public string CurrentDataScope => Principal?.FindFirstValue("scope") ?? "";

    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserHierarchy> UserHierarchy => Set<UserHierarchy>();
    public DbSet<Consultant> Consultants => Set<Consultant>();
    public DbSet<Resume> Resumes => Set<Resume>();
    public DbSet<Requirement> Requirements => Set<Requirement>();
    public DbSet<JobPosting> JobPostings => Set<JobPosting>();
    public DbSet<SavedRequirement> SavedRequirements => Set<SavedRequirement>();
    public DbSet<Submission> Submissions => Set<Submission>();
    public DbSet<SubmissionStageHistory> SubmissionStageHistory => Set<SubmissionStageHistory>();
    public DbSet<OnboardingItem> OnboardingItems => Set<OnboardingItem>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AiInteraction> AiInteractions => Set<AiInteraction>();
    public DbSet<ExternalJob> ExternalJobs => Set<ExternalJob>();
    public DbSet<ExternalJobSearch> ExternalJobSearches => Set<ExternalJobSearch>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<MailConnection> MailConnections => Set<MailConnection>();
    public DbSet<VendorEmail> VendorEmails => Set<VendorEmail>();
    public DbSet<EmailTemplate> EmailTemplates => Set<EmailTemplate>();
    public DbSet<FollowUpReminder> FollowUpReminders => Set<FollowUpReminder>();
    public DbSet<TailoredResume> TailoredResumes => Set<TailoredResume>();
    public DbSet<AutopilotItem> AutopilotItems => Set<AutopilotItem>();
    public DbSet<RtrRequest> RtrRequests => Set<RtrRequest>();
    public DbSet<AutopilotSettings> AutopilotSettings => Set<AutopilotSettings>();
    public DbSet<AutopilotNotification> AutopilotNotifications => Set<AutopilotNotification>();
    public DbSet<NotificationPrefs> NotificationPrefs => Set<NotificationPrefs>();

    /// <summary>SQL Server returns DATETIME2 without a kind; mark everything UTC so the browser shows correct local times.</summary>
    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<DateTime>().HaveConversion<UtcConverter>();   // also applies to DateTime?
        b.Properties<decimal>().HavePrecision(7, 2);
    }

    // JSON array columns (Skills, AcceptedVisas) ⇄ List<string>
    private static readonly ValueConverter<List<string>, string> JsonList = new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());
    private static readonly ValueComparer<List<string>> JsonListComparer = new(
        (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
        v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
        v => v.ToList());

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Role>(e => { e.ToTable("Roles", "sec"); e.HasKey(x => x.RoleId); e.Property(x => x.RoleId).ValueGeneratedNever(); });
        m.Entity<Permission>(e => { e.ToTable("Permissions", "sec"); e.HasKey(x => x.PermissionCode); });
        m.Entity<RolePermission>(e =>
        {
            e.ToTable("RolePermissions", "sec");
            e.HasKey(x => new { x.RoleId, x.PermissionCode });
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
        });
        m.Entity<Team>(e => { e.ToTable("Teams", "sec"); e.HasKey(x => x.TeamId); });
        m.Entity<User>(e =>
        {
            e.ToTable("Users", "sec");
            e.HasKey(x => x.UserId);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
            e.HasOne(x => x.Manager).WithMany().HasForeignKey(x => x.ManagerUserId);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId);
        });
        m.Entity<UserHierarchy>(e => { e.ToTable("UserHierarchy", "sec"); e.HasKey(x => new { x.AncestorUserId, x.DescendantUserId }); });

        m.Entity<Consultant>(e =>
        {
            e.ToTable("Consultants", "bench");
            e.HasKey(x => x.ConsultantId);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerUserId);
            e.Property(x => x.Skills).HasConversion(JsonList, JsonListComparer);
            e.Property(x => x.RowVer).IsRowVersion();   // optimistic concurrency
            // Team visibility. SQL Server 2012 has no row-level security, so EF applies the same rule as sec.fn_CanAccessOwner.
            e.HasQueryFilter(x => CurrentDataScope == "ALL"
                || (CurrentDataScope == "OWN" && x.OwnerUserId == CurrentUserId)
                || (CurrentDataScope == "TEAM" && UserHierarchy.Any(h => h.AncestorUserId == CurrentUserId && h.DescendantUserId == x.OwnerUserId)));
        });
        m.Entity<Resume>(e => { e.ToTable("Resumes", "bench"); e.HasKey(x => x.ResumeId); });
        m.Entity<Requirement>(e =>
        {
            e.ToTable("Requirements", "bench");
            e.HasKey(x => x.RequirementId);
            e.HasOne(x => x.PostedBy).WithMany().HasForeignKey(x => x.PostedByUserId);
            e.Property(x => x.Skills).HasConversion(JsonList, JsonListComparer);
            e.Property(x => x.AcceptedVisas).HasConversion(JsonList, JsonListComparer);
            e.Property(x => x.RowVer).IsRowVersion();
        });
        m.Entity<JobPosting>(e => { e.ToTable("JobPostings", "bench"); e.HasKey(x => x.PostingId); });
        m.Entity<SavedRequirement>(e => { e.ToTable("SavedRequirements", "bench"); e.HasKey(x => new { x.UserId, x.RequirementId }); });

        m.Entity<Submission>(e =>
        {
            e.ToTable("Submissions", "bench");
            e.HasKey(x => x.SubmissionId);

            // FIXED: Prevent multiple cascade paths by setting RESTRICT on Requirement and Owner
            e.HasOne(x => x.Consultant).WithMany().HasForeignKey(x => x.ConsultantId);

            e.HasOne(x => x.Requirement)
             .WithMany()
             .HasForeignKey(x => x.RequirementId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Owner)
             .WithMany()
             .HasForeignKey(x => x.OwnerUserId)
             .OnDelete(DeleteBehavior.Restrict);

            e.Property(x => x.RowVer).IsRowVersion();
            // One submission per consultant per requirement (UQ_Sub_ConsReq in 01_schema.sql): duplicates break the RTR.
            e.HasIndex(x => new { x.ConsultantId, x.RequirementId }).IsUnique().HasDatabaseName("UQ_Sub_ConsReq");
            e.HasQueryFilter(x => CurrentDataScope == "ALL"
                || (CurrentDataScope == "OWN" && x.OwnerUserId == CurrentUserId)
                || (CurrentDataScope == "TEAM" && UserHierarchy.Any(h => h.AncestorUserId == CurrentUserId && h.DescendantUserId == x.OwnerUserId)));
        });

        m.Entity<SubmissionStageHistory>(e => { e.ToTable("SubmissionStageHistory", "bench"); e.HasKey(x => x.HistoryId); });
        m.Entity<OnboardingItem>(e =>
        {
            e.ToTable("OnboardingItems", "bench");
            e.HasKey(x => new { x.SubmissionId, x.ItemCode });
            e.HasOne(x => x.DoneBy).WithMany().HasForeignKey(x => x.DoneByUserId);
        });
        m.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLog", "sec");
            e.HasKey(x => x.AuditId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });
        m.Entity<AiInteraction>(e => { e.ToTable("Interactions", "ai"); e.HasKey(x => x.InteractionId); });

        m.Entity<ExternalJob>(e =>
        {
            e.ToTable("ExternalJobs", "bench");
            e.HasKey(x => x.ExternalJobId);
            e.Property(x => x.Provider).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.ExternalId).HasMaxLength(200);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Company).HasMaxLength(200);
            e.Property(x => x.Location).HasMaxLength(200);
            e.Property(x => x.EmploymentType).HasMaxLength(40);
            e.Property(x => x.Publisher).HasMaxLength(100);
            e.Property(x => x.ApplyUrl).HasMaxLength(2000);
            e.Property(x => x.Skills).HasConversion(JsonList, JsonListComparer);
            e.Property(x => x.Visas).HasConversion(JsonList, JsonListComparer);
            e.Property(x => x.ApplyOptions).HasConversion(
                new ValueConverter<List<ApplyOption>, string>(v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                                                             v => JsonSerializer.Deserialize<List<ApplyOption>>(v, (JsonSerializerOptions?)null) ?? new List<ApplyOption>()),
                new ValueComparer<List<ApplyOption>>((a, b) => (a ?? new List<ApplyOption>()).SequenceEqual(b ?? new List<ApplyOption>()),
                                                     v => v.Aggregate(0, (h, o) => HashCode.Combine(h, o.GetHashCode())), v => v.ToList()));
            e.HasIndex(x => new { x.Provider, x.ExternalId }).IsUnique();
            e.HasIndex(x => x.LastSeenUtc);
            e.HasOne<Requirement>().WithMany().HasForeignKey(x => x.RequirementId).OnDelete(DeleteBehavior.SetNull);
        });
        m.Entity<Vendor>(e =>
        {
            e.ToTable("Vendors", "bench");
            e.HasKey(x => x.VendorId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Domains).HasConversion(JsonList, JsonListComparer);
        });
        m.Entity<AutopilotItem>(e =>
        {
            e.ToTable("AutopilotItems", "bench");
            e.HasKey(x => x.AutopilotItemId);
            e.Property(x => x.JobTitle).HasMaxLength(300);
            e.Property(x => x.Client).HasMaxLength(200);
            e.Property(x => x.Vendor).HasMaxLength(200);
            e.Property(x => x.Why).HasMaxLength(1000);
            e.Property(x => x.Status).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.ContractType).HasMaxLength(10);
            e.Property(x => x.VendorEmail).HasMaxLength(500);
            e.Property(x => x.EmailSubject).HasMaxLength(255);
            e.Property(x => x.Error).HasMaxLength(500);
            e.HasIndex(x => new { x.Status, x.OwnerUserId });
            e.HasIndex(x => new { x.ConsultantId, x.RequirementId, x.ExternalJobId });
        });
        m.Entity<RtrRequest>(e =>
        {
            e.ToTable("RtrRequests", "bench");
            e.HasKey(x => x.RtrRequestId);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.ConsultantEmail).HasMaxLength(256);
            e.Property(x => x.Status).HasMaxLength(12).IsUnicode(false);
            e.Property(x => x.SignedName).HasMaxLength(200);
            e.Property(x => x.DeclineReason).HasMaxLength(500);
            e.Property(x => x.RespondedIp).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(400);
            e.HasIndex(x => x.AutopilotItemId);
        });
        m.Entity<AutopilotNotification>(e =>
        {
            e.ToTable("AutopilotNotifications", "bench");
            e.HasKey(x => x.NotificationId);
            e.Property(x => x.Kind).HasMaxLength(12).IsUnicode(false);
            e.Property(x => x.Text).HasMaxLength(600);
            e.Property(x => x.Link).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.Error).HasMaxLength(300);
            e.HasIndex(x => new { x.Status, x.UserId });
        });
        m.Entity<NotificationPrefs>(e =>
        {
            e.ToTable("NotificationPrefs", "sec");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).ValueGeneratedNever();
            e.HasOne<User>().WithOne().HasForeignKey<NotificationPrefs>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<AutopilotSettings>(e =>
        {
            e.ToTable("AutopilotSettings", "bench");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.LastRunSummary).HasMaxLength(500);
        });
        m.Entity<TailoredResume>(e =>
        {
            e.ToTable("TailoredResumes", "bench");
            e.HasKey(x => x.TailoredResumeId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.TargetTitle).HasMaxLength(300);
            e.Property(x => x.TargetClient).HasMaxLength(200);
            e.Property(x => x.Engine).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.Notice).HasMaxLength(500);
            foreach (var p in new[] { nameof(TailoredResume.Changes), nameof(TailoredResume.Gaps), nameof(TailoredResume.Warnings), nameof(TailoredResume.Matched) })
                e.Property<List<string>>(p).HasConversion(JsonList, JsonListComparer);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ConsultantId, x.UpdatedUtc });
        });
        m.Entity<FollowUpReminder>(e =>
        {
            e.ToTable("FollowUpReminders", "bench");
            e.HasKey(x => x.ReminderId);
            e.HasOne(x => x.VendorEmail).WithMany().HasForeignKey(x => x.VendorEmailId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ConversationKey).HasMaxLength(400);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(12).IsUnicode(false);
            e.HasIndex(x => new { x.UserId, x.Status, x.DueUtc });
            e.HasIndex(x => x.VendorEmailId);
        });
        m.Entity<EmailTemplate>(e =>
        {
            e.ToTable("EmailTemplates", "bench");
            e.HasKey(x => x.TemplateId);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Category).HasMaxLength(30);
            e.Property(x => x.Subject).HasMaxLength(255);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.IsShared, x.OwnerUserId });
        });
        m.Entity<MailConnection>(e =>
        {
            e.ToTable("MailConnections", "sec");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).ValueGeneratedNever();
            e.HasOne<User>().WithOne().HasForeignKey<MailConnection>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Provider).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.Mailbox).HasMaxLength(256);
            e.Property(x => x.Status).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.Property(x => x.GrantedScopes).HasMaxLength(1000);
        });
        m.Entity<VendorEmail>(e =>
        {
            e.ToTable("VendorEmails", "bench");
            e.HasKey(x => x.VendorEmailId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Vendor).WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.MessageId).HasMaxLength(400);
            e.Property(x => x.ConversationId).HasMaxLength(400);
            e.Property(x => x.Direction).HasMaxLength(3).IsUnicode(false);
            e.Property(x => x.FromAddress).HasMaxLength(256);
            e.Property(x => x.FromName).HasMaxLength(200);
            e.Property(x => x.ToAddresses).HasMaxLength(1000);
            e.Property(x => x.Subject).HasMaxLength(400);
            e.Property(x => x.Preview).HasMaxLength(200);
            e.Property(x => x.Attachments).HasConversion(JsonList, JsonListComparer);
            e.Property(x => x.Intent).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.IntentSummary).HasMaxLength(300);
            e.HasIndex(x => new { x.UserId, x.MessageId }).IsUnique();
            e.HasIndex(x => new { x.VendorId, x.SentUtc });
            e.HasIndex(x => new { x.UserId, x.SentUtc });
        });
        m.Entity<ExternalJobSearch>(e =>
        {
            e.ToTable("ExternalJobSearches", "bench");
            e.HasKey(x => x.SearchId);
            e.Property(x => x.Provider).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.QueryKey).HasMaxLength(400);
            e.Property(x => x.Query).HasMaxLength(300);
            e.Property(x => x.Location).HasMaxLength(200);
            e.Property(x => x.Error).HasMaxLength(500);
            e.Property(x => x.JobIds).HasConversion(
                new ValueConverter<List<int>, string>(v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                                                     v => JsonSerializer.Deserialize<List<int>>(v, (JsonSerializerOptions?)null) ?? new List<int>()),
                new ValueComparer<List<int>>((a, b) => (a ?? new List<int>()).SequenceEqual(b ?? new List<int>()), v => v.Aggregate(0, HashCode.Combine), v => v.ToList()));
            e.HasIndex(x => new { x.QueryKey, x.CreatedUtc });
            e.HasIndex(x => x.CreatedUtc);
        });
    }
}

/// <summary>
/// Every time EF opens a connection, tell SQL Server who the user is, for the stored procedures
/// (sec.fn_CurrentUserId in 03_sqlserver2012.sql). Uses CONTEXT_INFO because sp_set_session_context
/// needs SQL Server 2016+. Always set (0 = nobody) so a pooled connection never keeps a previous user.
/// </summary>
public sealed class SessionContextInterceptor(IHttpContextAccessor http) : DbConnectionInterceptor
{
    private int UserId =>
        int.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    private DbCommand Command(DbConnection connection)
    {
        var id = UserId;
        var cmd = connection.CreateCommand();
        cmd.CommandText = "DECLARE @ctx VARBINARY(128) = CAST(@id AS VARBINARY(4)); SET CONTEXT_INFO @ctx;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.DbType = DbType.Int32;
        p.Value = id;
        cmd.Parameters.Add(p);
        return cmd;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = Command(connection);
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = Command(connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
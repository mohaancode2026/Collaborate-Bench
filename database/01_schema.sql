/* =====================================================================
   Collaborate-Bench Sales — database schema
   Target : SQL Server 2022+ / Azure SQL Database
            (the optional VECTOR columns at the end need SQL Server 2025
             or Azure SQL; skip that section on older versions)
   Run    : sqlcmd -S <server> -d CollaborateBench -i 01_schema.sql
   ===================================================================== */
SET NOCOUNT ON;
GO
CREATE SCHEMA sec;   -- identity, roles, permissions, hierarchy, audit
GO
CREATE SCHEMA bench; -- business data
GO
CREATE SCHEMA ai;    -- AI usage
GO

/* ---------- 1. Roles & permissions ---------- */
CREATE TABLE sec.Roles (
    RoleId    TINYINT      NOT NULL CONSTRAINT PK_Roles PRIMARY KEY,
    Name      NVARCHAR(50) NOT NULL CONSTRAINT UQ_Roles_Name UNIQUE,
    DataScope VARCHAR(4)   NOT NULL CONSTRAINT CK_Roles_Scope CHECK (DataScope IN ('ALL','TEAM','OWN')),
    Rank      TINYINT      NOT NULL
);
INSERT sec.Roles VALUES (1,N'Admin','ALL',100),(2,N'Delivery Manager','ALL',80),(3,N'Team Lead','TEAM',60),(4,N'Recruiter','OWN',40);

CREATE TABLE sec.Permissions (
    PermissionCode VARCHAR(40)   NOT NULL CONSTRAINT PK_Permissions PRIMARY KEY,
    Description    NVARCHAR(200) NOT NULL
);
INSERT sec.Permissions VALUES
 ('dash.view',N'view the dashboard'),('bench.view',N'view the bench'),('bench.create',N'add consultants'),
 ('bench.edit',N'edit consultants'),('bench.delete',N'remove consultants'),('bench.reassign',N'reassign consultants'),
 ('parser.use',N'use the AI resume parser'),('jobs.view',N'view requirements'),('jobs.create',N'post requirements'),
 ('jobs.close',N'close or reopen requirements'),('board.view',N'search the job board'),('match.view',N'use the match agent'),
 ('pipeline.view',N'view submissions'),('sub.create',N'submit consultants'),('sub.move',N'move submissions between stages'),
 ('sub.withdraw',N'withdraw submissions'),('onboard.view',N'view onboarding'),('onboard.edit',N'manage MSA/PO, onboarding and billing'),
 ('rates.view',N'use the rate & margin calculator'),('margin.view',N'see margins'),('team.view',N'view the team'),
 ('team.manage',N'manage users and roles'),('audit.view',N'view the audit log'),('ai.copilot',N'use the AI copilot');

CREATE TABLE sec.RolePermissions (
    RoleId         TINYINT     NOT NULL CONSTRAINT FK_RP_Role REFERENCES sec.Roles(RoleId),
    PermissionCode VARCHAR(40) NOT NULL CONSTRAINT FK_RP_Perm REFERENCES sec.Permissions(PermissionCode),
    CONSTRAINT PK_RolePermissions PRIMARY KEY (RoleId, PermissionCode)
);
INSERT sec.RolePermissions SELECT 1, PermissionCode FROM sec.Permissions;
INSERT sec.RolePermissions SELECT 2, PermissionCode FROM sec.Permissions WHERE PermissionCode NOT IN ('parser.use','bench.create');
INSERT sec.RolePermissions SELECT 3, PermissionCode FROM sec.Permissions WHERE PermissionCode NOT IN ('team.manage','team.view','parser.use','bench.create');
INSERT sec.RolePermissions SELECT 4, PermissionCode FROM sec.Permissions WHERE PermissionCode IN
 ('dash.view','bench.view','bench.create','bench.edit','jobs.view','jobs.create','board.view',
  'match.view','pipeline.view','sub.create','sub.move','onboard.view','ai.copilot');

/* ---------- 2. Teams, users, hierarchy ---------- */
CREATE TABLE sec.Teams (
    TeamId   INT IDENTITY CONSTRAINT PK_Teams PRIMARY KEY,
    Name     NVARCHAR(80) NOT NULL CONSTRAINT UQ_Teams_Name UNIQUE
);

CREATE TABLE sec.Users (
    UserId             INT IDENTITY   CONSTRAINT PK_Users PRIMARY KEY,
    Email              NVARCHAR(256)  NOT NULL CONSTRAINT UQ_Users_Email UNIQUE,
    FullName           NVARCHAR(120)  NOT NULL,
    RoleId             TINYINT        NOT NULL CONSTRAINT FK_Users_Role REFERENCES sec.Roles(RoleId),
    ManagerUserId      INT            NULL     CONSTRAINT FK_Users_Manager REFERENCES sec.Users(UserId),
    TeamId             INT            NULL     CONSTRAINT FK_Users_Team REFERENCES sec.Teams(TeamId),
    PasswordHash       NVARCHAR(200)  NULL,    -- ASP.NET Core Identity PasswordHasher (PBKDF2)
    MustChangePassword BIT            NOT NULL CONSTRAINT DF_Users_MustChange DEFAULT 1,
    IsActive           BIT            NOT NULL CONSTRAINT DF_Users_Active DEFAULT 1,
    FailedLoginCount   TINYINT        NOT NULL CONSTRAINT DF_Users_Fail DEFAULT 0,
    LockoutEndUtc      DATETIME2(0)   NULL,
    LastLoginUtc       DATETIME2(0)   NULL,
    CreatedUtc         DATETIME2(0)   NOT NULL CONSTRAINT DF_Users_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Users_NotOwnManager CHECK (ManagerUserId IS NULL OR ManagerUserId <> UserId)
);
CREATE INDEX IX_Users_Manager ON sec.Users(ManagerUserId);

/* Closure table: (ancestor, descendant) incl. self. Used by RLS and team scope. */
CREATE TABLE sec.UserHierarchy (
    AncestorUserId   INT     NOT NULL,
    DescendantUserId INT     NOT NULL,
    Depth            TINYINT NOT NULL,
    CONSTRAINT PK_UserHierarchy PRIMARY KEY (AncestorUserId, DescendantUserId)
);
GO
CREATE OR ALTER PROCEDURE sec.usp_RebuildHierarchy
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRAN;
        DELETE sec.UserHierarchy;
        WITH h AS (
            SELECT UserId AS AncestorUserId, UserId AS DescendantUserId, CAST(0 AS TINYINT) AS Depth FROM sec.Users
            UNION ALL
            SELECT h.AncestorUserId, u.UserId, CAST(h.Depth + 1 AS TINYINT)
            FROM h JOIN sec.Users u ON u.ManagerUserId = h.DescendantUserId
        )
        INSERT sec.UserHierarchy SELECT AncestorUserId, DescendantUserId, Depth FROM h OPTION (MAXRECURSION 20);
    COMMIT;
END
GO

/* ---------- 3. Business data ---------- */
CREATE TABLE bench.Consultants (
    ConsultantId    INT IDENTITY   CONSTRAINT PK_Consultants PRIMARY KEY,
    OwnerUserId     INT            NOT NULL CONSTRAINT FK_Cons_Owner REFERENCES sec.Users(UserId),
    FullName        NVARCHAR(120)  NOT NULL,
    Title           NVARCHAR(120)  NULL,
    Email           NVARCHAR(256)  NULL,
    Phone           VARCHAR(30)    NULL,
    VisaStatus      VARCHAR(12)    NULL CONSTRAINT CK_Cons_Visa CHECK (VisaStatus IN
                    ('USC','GC','GC-EAD','H1B','H4-EAD','L2-EAD','OPT','CPT','STEM OPT','TN','EAD')),
    Location        NVARCHAR(120)  NULL,
    OpenToRelocate  BIT            NOT NULL CONSTRAINT DF_Cons_Reloc DEFAULT 0,
    PayRate         DECIMAL(7,2)   NOT NULL CONSTRAINT CK_Cons_Rate CHECK (PayRate BETWEEN 1 AND 500),
    ExperienceYears TINYINT        NULL CONSTRAINT CK_Cons_Exp CHECK (ExperienceYears <= 60),
    Status          VARCHAR(10)    NOT NULL CONSTRAINT DF_Cons_Status DEFAULT 'Bench'
                    CONSTRAINT CK_Cons_Status CHECK (Status IN ('Bench','Marketing','Placed','Inactive')),
    Skills          NVARCHAR(1000) NOT NULL CONSTRAINT DF_Cons_Skills DEFAULT N'[]' CONSTRAINT CK_Cons_SkillsJson CHECK (ISJSON(Skills) = 1),
    HotlistSummary  NVARCHAR(600)  NULL,
    CreatedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_Cons_Created DEFAULT SYSUTCDATETIME(),
    UpdatedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_Cons_Updated DEFAULT SYSUTCDATETIME(),
    RowVer          ROWVERSION
);
CREATE UNIQUE INDEX UX_Cons_Email ON bench.Consultants(Email) WHERE Email IS NOT NULL AND Status <> 'Inactive';
CREATE INDEX IX_Cons_Owner_Status ON bench.Consultants(OwnerUserId, Status);

CREATE TABLE bench.Resumes (
    ResumeId         INT IDENTITY  CONSTRAINT PK_Resumes PRIMARY KEY,
    ConsultantId     INT           NOT NULL CONSTRAINT FK_Res_Cons REFERENCES bench.Consultants(ConsultantId),
    FileName         NVARCHAR(200) NULL,
    BlobUri          NVARCHAR(400) NULL,  -- original file in Azure Blob Storage (optional)
    ExtractedText    NVARCHAR(MAX) NOT NULL,
    ParsedBy         VARCHAR(10)   NOT NULL,  -- claude | rules
    UploadedByUserId INT           NOT NULL CONSTRAINT FK_Res_User REFERENCES sec.Users(UserId),
    UploadedUtc      DATETIME2(0)  NOT NULL CONSTRAINT DF_Res_Up DEFAULT SYSUTCDATETIME()
);

CREATE TABLE bench.Requirements (   -- shared across all teams (no RLS)
    RequirementId  INT IDENTITY   CONSTRAINT PK_Requirements PRIMARY KEY,
    Title          NVARCHAR(150)  NOT NULL,
    EndClient      NVARCHAR(150)  NOT NULL,
    Vendor         NVARCHAR(150)  NOT NULL CONSTRAINT DF_Req_Vendor DEFAULT N'Direct client',
    Tier           VARCHAR(30)    NOT NULL CONSTRAINT CK_Req_Tier CHECK (Tier IN ('Direct','Prime vendor','Implementation partner','Tier-1','Tier-2')),
    Location       NVARCHAR(120)  NOT NULL,
    WorkMode       VARCHAR(10)    NOT NULL CONSTRAINT CK_Req_Mode CHECK (WorkMode IN ('Remote','Hybrid','Onsite')),
    ContractType   VARCHAR(5)     NOT NULL CONSTRAINT CK_Req_Type CHECK (ContractType IN ('C2C','W2','1099')),
    BillRate       DECIMAL(7,2)   NOT NULL CONSTRAINT CK_Req_Rate CHECK (BillRate BETWEEN 10 AND 400),
    Skills         NVARCHAR(1000) NOT NULL CONSTRAINT CK_Req_SkillsJson CHECK (ISJSON(Skills) = 1),
    AcceptedVisas  NVARCHAR(200)  NOT NULL CONSTRAINT DF_Req_Visa DEFAULT N'["Any"]' CONSTRAINT CK_Req_VisaJson CHECK (ISJSON(AcceptedVisas) = 1),
    Description    NVARCHAR(MAX)  NULL,
    Source         VARCHAR(20)    NOT NULL CONSTRAINT DF_Req_Src DEFAULT 'Manual',
    Status         VARCHAR(10)    NOT NULL CONSTRAINT DF_Req_Status DEFAULT 'Open' CONSTRAINT CK_Req_Status CHECK (Status IN ('Open','Closed')),
    PostedByUserId INT            NOT NULL CONSTRAINT FK_Req_User REFERENCES sec.Users(UserId),
    PostedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_Req_Posted DEFAULT SYSUTCDATETIME(),
    RowVer         ROWVERSION
);
CREATE INDEX IX_Req_Status ON bench.Requirements(Status, PostedUtc DESC);

CREATE TABLE bench.JobPostings (    -- posting-agent distribution queue
    PostingId     INT IDENTITY CONSTRAINT PK_JobPostings PRIMARY KEY,
    RequirementId INT          NOT NULL CONSTRAINT FK_JP_Req REFERENCES bench.Requirements(RequirementId),
    Channel       VARCHAR(30)  NOT NULL,
    Status        VARCHAR(10)  NOT NULL CONSTRAINT DF_JP_Status DEFAULT 'Queued' CONSTRAINT CK_JP_Status CHECK (Status IN ('Queued','Posted','Failed')),
    ErrorMessage  NVARCHAR(400) NULL,
    CreatedUtc    DATETIME2(0) NOT NULL CONSTRAINT DF_JP_Created DEFAULT SYSUTCDATETIME()
);

CREATE TABLE bench.SavedRequirements (
    UserId        INT NOT NULL CONSTRAINT FK_SR_User REFERENCES sec.Users(UserId),
    RequirementId INT NOT NULL CONSTRAINT FK_SR_Req  REFERENCES bench.Requirements(RequirementId),
    CONSTRAINT PK_SavedRequirements PRIMARY KEY (UserId, RequirementId)
);

CREATE TABLE bench.Submissions (
    SubmissionId    INT IDENTITY   CONSTRAINT PK_Submissions PRIMARY KEY,
    ConsultantId    INT            NOT NULL CONSTRAINT FK_Sub_Cons REFERENCES bench.Consultants(ConsultantId),
    RequirementId   INT            NOT NULL CONSTRAINT FK_Sub_Req  REFERENCES bench.Requirements(RequirementId),
    OwnerUserId     INT            NOT NULL CONSTRAINT FK_Sub_Owner REFERENCES sec.Users(UserId),
    Stage           VARCHAR(20)    NOT NULL CONSTRAINT DF_Sub_Stage DEFAULT 'Submitted'
                    CONSTRAINT CK_Sub_Stage CHECK (Stage IN ('Submitted','Vendor Screen','Client Interview','Offer','MSA / PO','Billing','Withdrawn')),
    SubmitRate      DECIMAL(7,2)   NOT NULL,
    PayRate         DECIMAL(7,2)   NOT NULL,
    MatchScore      TINYINT        NULL,
    RtrSigned       BIT            NOT NULL,
    Notes           NVARCHAR(1000) NULL,
    StageChangedUtc DATETIME2(0)   NOT NULL CONSTRAINT DF_Sub_StageUtc DEFAULT SYSUTCDATETIME(),
    CreatedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_Sub_Created DEFAULT SYSUTCDATETIME(),
    RowVer          ROWVERSION,
    CONSTRAINT UQ_Sub_ConsReq UNIQUE (ConsultantId, RequirementId),  -- no duplicate submissions
    CONSTRAINT CK_Sub_Rtr     CHECK (RtrSigned = 1),                 -- RTR is mandatory
    CONSTRAINT CK_Sub_Margin  CHECK (SubmitRate > PayRate)
);
CREATE INDEX IX_Sub_Owner_Stage ON bench.Submissions(OwnerUserId, Stage);

CREATE TABLE bench.SubmissionStageHistory (
    HistoryId       BIGINT IDENTITY CONSTRAINT PK_SSH PRIMARY KEY,
    SubmissionId    INT          NOT NULL CONSTRAINT FK_SSH_Sub REFERENCES bench.Submissions(SubmissionId),
    FromStage       VARCHAR(20)  NULL,
    ToStage         VARCHAR(20)  NOT NULL,
    ChangedByUserId INT          NOT NULL,
    ChangedUtc      DATETIME2(0) NOT NULL CONSTRAINT DF_SSH_Utc DEFAULT SYSUTCDATETIME()
);

CREATE TABLE bench.OnboardingItems (
    SubmissionId INT          NOT NULL CONSTRAINT FK_OI_Sub REFERENCES bench.Submissions(SubmissionId),
    ItemCode     VARCHAR(20)  NOT NULL CONSTRAINT CK_OI_Code CHECK (ItemCode IN ('MSA','PO','RTR_OFFER','I9','BGV','DRUG','ACCESS','TIMESHEET','INVOICE')),
    IsDone       BIT          NOT NULL,
    DoneByUserId INT          NULL,
    DoneUtc      DATETIME2(0) NULL,
    CONSTRAINT PK_OnboardingItems PRIMARY KEY (SubmissionId, ItemCode)
);

/* ---------- 4. Audit & AI usage ---------- */
CREATE TABLE sec.AuditLog (
    AuditId       BIGINT IDENTITY CONSTRAINT PK_AuditLog PRIMARY KEY,
    UserId        INT            NULL,
    Action        VARCHAR(40)    NOT NULL,
    EntityType    VARCHAR(30)    NULL,
    EntityId      INT            NULL,
    Outcome       VARCHAR(10)    NOT NULL CONSTRAINT CK_Audit_Outcome CHECK (Outcome IN ('Success','Denied','Error')),
    Detail        NVARCHAR(1000) NULL,
    IpAddress     VARCHAR(45)    NULL,
    CreatedUtc    DATETIME2(3)   NOT NULL CONSTRAINT DF_Audit_Utc DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Audit_Time ON sec.AuditLog(CreatedUtc DESC) INCLUDE (UserId, Action, Outcome);

CREATE TABLE ai.Interactions (
    InteractionId BIGINT IDENTITY CONSTRAINT PK_AiInteractions PRIMARY KEY,
    UserId        INT          NULL,
    Feature       VARCHAR(30)  NOT NULL,
    Model         VARCHAR(60)  NOT NULL,
    InputTokens   INT          NULL,
    OutputTokens  INT          NULL,
    LatencyMs     INT          NULL,
    Succeeded     BIT          NOT NULL,
    ErrorCode     VARCHAR(40)  NULL,
    CreatedUtc    DATETIME2(0) NOT NULL CONSTRAINT DF_AiI_Utc DEFAULT SYSUTCDATETIME()
);
GO

/* ---------- 5. Row-level security ----------
   The API runs  sp_set_session_context 'UserId'  on every connection, so SQL Server
   itself limits consultants and submissions to: Recruiter = own rows,
   Team Lead = own + everyone below them, Delivery Manager/Admin = all rows. */
CREATE OR ALTER FUNCTION sec.fn_CanAccessOwner(@OwnerUserId INT)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS Allowed
    FROM sec.Users me
    JOIN sec.Roles r ON r.RoleId = me.RoleId
    WHERE me.UserId = CAST(SESSION_CONTEXT(N'UserId') AS INT)
      AND me.IsActive = 1
      AND (   r.DataScope = 'ALL'
           OR (r.DataScope = 'OWN'  AND @OwnerUserId = me.UserId)
           OR (r.DataScope = 'TEAM' AND EXISTS (SELECT 1 FROM sec.UserHierarchy h
                                                WHERE h.AncestorUserId = me.UserId AND h.DescendantUserId = @OwnerUserId)));
GO
CREATE SECURITY POLICY sec.TeamDataPolicy
    ADD FILTER PREDICATE sec.fn_CanAccessOwner(OwnerUserId) ON bench.Consultants,
    ADD BLOCK  PREDICATE sec.fn_CanAccessOwner(OwnerUserId) ON bench.Consultants AFTER INSERT,
    ADD BLOCK  PREDICATE sec.fn_CanAccessOwner(OwnerUserId) ON bench.Consultants AFTER UPDATE,
    ADD FILTER PREDICATE sec.fn_CanAccessOwner(OwnerUserId) ON bench.Submissions,
    ADD BLOCK  PREDICATE sec.fn_CanAccessOwner(OwnerUserId) ON bench.Submissions AFTER INSERT,
    ADD BLOCK  PREDICATE sec.fn_CanAccessOwner(OwnerUserId) ON bench.Submissions AFTER UPDATE
WITH (STATE = ON);
GO

CREATE OR ALTER FUNCTION sec.fn_HasPermission(@UserId INT, @Perm VARCHAR(40))
RETURNS BIT
AS
BEGIN
    RETURN (SELECT CASE WHEN EXISTS (
              SELECT 1 FROM sec.Users u JOIN sec.RolePermissions rp ON rp.RoleId = u.RoleId
              WHERE u.UserId = @UserId AND u.IsActive = 1 AND rp.PermissionCode = @Perm) THEN 1 ELSE 0 END);
END
GO

/* ---------- 6. Business-rule procedure ----------
   THROW numbers 50001-50099 are turned into user-facing messages by the API:
   message format is  'CODE: human readable detail'. */
CREATE OR ALTER PROCEDURE bench.usp_MoveSubmissionStage
    @SubmissionId INT, @ToStage VARCHAR(20), @ExpectedRowVer VARBINARY(8) = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @Me INT = CAST(SESSION_CONTEXT(N'UserId') AS INT), @From VARCHAR(20), @Ver VARBINARY(8), @ConsultantId INT, @RequirementId INT;

    IF sec.fn_HasPermission(@Me, 'sub.move') = 0
        THROW 50001, 'PERMISSION_DENIED: Your role cannot move submissions between stages.', 1;
    IF @ToStage NOT IN ('Submitted','Vendor Screen','Client Interview','Offer','MSA / PO','Billing')
        THROW 50011, 'INVALID_STAGE: That stage does not exist.', 1;

    BEGIN TRAN;
        SELECT @From = Stage, @Ver = RowVer, @ConsultantId = ConsultantId, @RequirementId = RequirementId
        FROM bench.Submissions WITH (UPDLOCK) WHERE SubmissionId = @SubmissionId;   -- RLS hides other teams

        IF @From IS NULL OR @From = 'Withdrawn'
            THROW 50004, 'NOT_FOUND: The submission was not found, was withdrawn, or is outside your team.', 1;
        IF @ExpectedRowVer IS NOT NULL AND @Ver <> @ExpectedRowVer
            THROW 50009, 'CONFLICT: A teammate changed this submission a moment ago. Refresh and try again.', 1;
        IF (@ToStage IN ('MSA / PO','Billing') OR @From IN ('MSA / PO','Billing')) AND sec.fn_HasPermission(@Me, 'onboard.edit') = 0
            THROW 50002, 'PERMISSION_DENIED: MSA / PO and Billing stages need a Team Lead or above.', 1;
        IF @ToStage = 'Billing' AND (SELECT COUNT(*) FROM bench.OnboardingItems
                                     WHERE SubmissionId = @SubmissionId AND IsDone = 1
                                       AND ItemCode IN ('MSA','PO','RTR_OFFER','I9','BGV','DRUG')) < 6
            THROW 50010, 'ONBOARDING_INCOMPLETE: Finish MSA, PO, RTR/offer, I-9, background check and drug screen before billing.', 1;

        IF @From <> @ToStage
        BEGIN
            UPDATE bench.Submissions SET Stage = @ToStage, StageChangedUtc = SYSUTCDATETIME() WHERE SubmissionId = @SubmissionId;
            INSERT bench.SubmissionStageHistory(SubmissionId, FromStage, ToStage, ChangedByUserId) VALUES (@SubmissionId, @From, @ToStage, @Me);
            IF @ToStage = 'Billing'
            BEGIN
                UPDATE bench.Consultants  SET Status = 'Placed', UpdatedUtc = SYSUTCDATETIME() WHERE ConsultantId = @ConsultantId;
                UPDATE bench.Requirements SET Status = 'Closed' WHERE RequirementId = @RequirementId;
            END
            INSERT sec.AuditLog(UserId, Action, EntityType, EntityId, Outcome, Detail)
            SELECT @Me, 'STAGE_MOVE', 'Submission', @SubmissionId, 'Success',
                   CONCAT(c.FullName, N' → ', @ToStage, N' (', r.Title, N')')
            FROM bench.Consultants c, bench.Requirements r WHERE c.ConsultantId = @ConsultantId AND r.RequirementId = @RequirementId;
        END
    COMMIT;
END
GO

/* ---------- 7. OPTIONAL (SQL Server 2025 / Azure SQL): semantic matching ----------
ALTER TABLE bench.Consultants  ADD Embedding VECTOR(1536) NULL;
ALTER TABLE bench.Requirements ADD Embedding VECTOR(1536) NULL;
-- SELECT TOP 10 c.ConsultantId, 1 - VECTOR_DISTANCE('cosine', c.Embedding, r.Embedding) AS Similarity
-- FROM bench.Consultants c CROSS JOIN bench.Requirements r WHERE r.RequirementId = @Id ORDER BY 2 DESC;
*/

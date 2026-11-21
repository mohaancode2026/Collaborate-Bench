/* =====================================================================
   Collaborate-Bench Sales — SQL Server 2012 compatibility objects
   Target : SQL Server 2012 (compatibility level 110) and later
   Run    : sqlcmd -S <server> -d CollaborateBench -I -i 03_sqlserver2012.sql
            (after the tables exist: EF migration or 01_schema.sql minus RLS)

   01_schema.sql needs SQL Server 2016+ (SESSION_CONTEXT, row-level
   security, CREATE OR ALTER, ISJSON). On 2012:
     - the API puts the signed-in user id in CONTEXT_INFO instead of
       SESSION_CONTEXT (see SessionContextInterceptor),
     - team filtering of consultants/submissions is done by EF Core global
       query filters in AppDbContext instead of a SECURITY POLICY,
     - the procedures below read the user from CONTEXT_INFO and check team
       access themselves.
   Safe to re-run.
   ===================================================================== */
SET NOCOUNT ON;
GO

/* ---------- Current user (set by the API on every connection) ---------- */
IF OBJECT_ID('sec.fn_CurrentUserId') IS NOT NULL DROP FUNCTION sec.fn_CurrentUserId;
GO
CREATE FUNCTION sec.fn_CurrentUserId()
RETURNS INT
AS
BEGIN
    RETURN CASE WHEN CONTEXT_INFO() IS NULL THEN NULL
                ELSE NULLIF(CAST(SUBSTRING(CONTEXT_INFO(), 1, 4) AS INT), 0) END;
END
GO

/* ---------- Hierarchy closure table ---------- */
IF OBJECT_ID('sec.usp_RebuildHierarchy') IS NOT NULL DROP PROCEDURE sec.usp_RebuildHierarchy;
GO
CREATE PROCEDURE sec.usp_RebuildHierarchy
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
        INSERT sec.UserHierarchy(AncestorUserId, DescendantUserId, Depth)
        SELECT AncestorUserId, DescendantUserId, Depth FROM h OPTION (MAXRECURSION 20);
    COMMIT;
END
GO

/* ---------- Access rules (same logic as the 2016+ RLS predicate) ---------- */
IF OBJECT_ID('sec.fn_CanAccessOwner') IS NOT NULL DROP FUNCTION sec.fn_CanAccessOwner;
GO
CREATE FUNCTION sec.fn_CanAccessOwner(@OwnerUserId INT)
RETURNS TABLE
AS
RETURN
    SELECT 1 AS Allowed
    FROM sec.Users me
    JOIN sec.Roles r ON r.RoleId = me.RoleId
    WHERE me.UserId = sec.fn_CurrentUserId()
      AND me.IsActive = 1
      AND (   r.DataScope = 'ALL'
           OR (r.DataScope = 'OWN'  AND @OwnerUserId = me.UserId)
           OR (r.DataScope = 'TEAM' AND EXISTS (SELECT 1 FROM sec.UserHierarchy h
                                                WHERE h.AncestorUserId = me.UserId AND h.DescendantUserId = @OwnerUserId)));
GO

IF OBJECT_ID('sec.fn_HasPermission') IS NOT NULL DROP FUNCTION sec.fn_HasPermission;
GO
CREATE FUNCTION sec.fn_HasPermission(@UserId INT, @Perm VARCHAR(40))
RETURNS BIT
AS
BEGIN
    RETURN (SELECT CASE WHEN EXISTS (
              SELECT 1 FROM sec.Users u JOIN sec.RolePermissions rp ON rp.RoleId = u.RoleId
              WHERE u.UserId = @UserId AND u.IsActive = 1 AND rp.PermissionCode = @Perm) THEN 1 ELSE 0 END);
END
GO

/* ---------- Business-rule procedure ----------
   THROW numbers 50001-50099 are turned into user-facing messages by the API. */
IF OBJECT_ID('bench.usp_MoveSubmissionStage') IS NOT NULL DROP PROCEDURE bench.usp_MoveSubmissionStage;
GO
CREATE PROCEDURE bench.usp_MoveSubmissionStage
    @SubmissionId INT, @ToStage VARCHAR(20), @ExpectedRowVer VARBINARY(8) = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @Me INT = sec.fn_CurrentUserId(), @From VARCHAR(20), @Ver VARBINARY(8),
            @ConsultantId INT, @RequirementId INT, @OwnerUserId INT, @Now DATETIME2(0) = SYSUTCDATETIME();

    IF sec.fn_HasPermission(@Me, 'sub.move') = 0
        THROW 50001, 'PERMISSION_DENIED: Your role cannot move submissions between stages.', 1;
    IF @ToStage NOT IN ('Submitted','Vendor Screen','Client Interview','Offer','MSA / PO','Billing')
        THROW 50011, 'INVALID_STAGE: That stage does not exist.', 1;

    BEGIN TRAN;
        SELECT @From = Stage, @Ver = RowVer, @ConsultantId = ConsultantId, @RequirementId = RequirementId, @OwnerUserId = OwnerUserId
        FROM bench.Submissions WITH (UPDLOCK) WHERE SubmissionId = @SubmissionId;

        -- No row-level security on 2012: hide other teams' submissions here.
        IF @From IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sec.fn_CanAccessOwner(@OwnerUserId))
            SET @From = NULL;

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
            UPDATE bench.Submissions SET Stage = @ToStage, StageChangedUtc = @Now WHERE SubmissionId = @SubmissionId;
            INSERT bench.SubmissionStageHistory(SubmissionId, FromStage, ToStage, ChangedByUserId, ChangedUtc)
            VALUES (@SubmissionId, @From, @ToStage, @Me, @Now);
            IF @ToStage = 'Billing'
            BEGIN
                UPDATE bench.Consultants  SET Status = 'Placed', UpdatedUtc = @Now WHERE ConsultantId = @ConsultantId;
                UPDATE bench.Requirements SET Status = 'Closed' WHERE RequirementId = @RequirementId;
            END
            INSERT sec.AuditLog(UserId, Action, EntityType, EntityId, Outcome, Detail, CreatedUtc)
            SELECT @Me, 'STAGE_MOVE', 'Submission', @SubmissionId, 'Success',
                   CONCAT(c.FullName, N' → ', @ToStage, N' (', r.Title, N')'), @Now
            FROM bench.Consultants c, bench.Requirements r WHERE c.ConsultantId = @ConsultantId AND r.RequirementId = @RequirementId;
        END
    COMMIT;
END
GO

/* ---------- Fill the hierarchy for existing users ---------- */
EXEC sec.usp_RebuildHierarchy;
GO

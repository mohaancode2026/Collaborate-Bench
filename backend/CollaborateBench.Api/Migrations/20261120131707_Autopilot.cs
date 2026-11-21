using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class Autopilot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Intent",
                schema: "bench",
                table: "VendorEmails",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IntentHandled",
                schema: "bench",
                table: "VendorEmails",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IntentSummary",
                schema: "bench",
                table: "VendorEmails",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubmissionId",
                schema: "bench",
                table: "VendorEmails",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutopilotItems",
                schema: "bench",
                columns: table => new
                {
                    AutopilotItemId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConsultantId = table.Column<int>(type: "int", nullable: false),
                    OwnerUserId = table.Column<int>(type: "int", nullable: false),
                    RequirementId = table.Column<int>(type: "int", nullable: true),
                    ExternalJobId = table.Column<int>(type: "int", nullable: true),
                    JobTitle = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Client = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Vendor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Score = table.Column<int>(type: "int", nullable: false),
                    Why = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    TailoredResumeId = table.Column<int>(type: "int", nullable: true),
                    BillRate = table.Column<decimal>(type: "decimal(7,2)", precision: 7, scale: 2, nullable: true),
                    SubmitRate = table.Column<decimal>(type: "decimal(7,2)", precision: 7, scale: 2, nullable: true),
                    PayRate = table.Column<decimal>(type: "decimal(7,2)", precision: 7, scale: 2, nullable: true),
                    ContractType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    VendorEmail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EmailSubject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    EmailBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ApprovedByUserId = table.Column<int>(type: "int", nullable: true),
                    SubmissionId = table.Column<int>(type: "int", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ApprovedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotItems", x => x.AutopilotItemId);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotSettings",
                schema: "bench",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    MinScore = table.Column<int>(type: "int", nullable: false),
                    MaxNewPerConsultant = table.Column<int>(type: "int", nullable: false),
                    MaxPreparePerRun = table.Column<int>(type: "int", nullable: false),
                    IncludeExternalJobs = table.Column<bool>(type: "bit", nullable: false),
                    AutoFollowUp = table.Column<bool>(type: "bit", nullable: false),
                    MaxAutoFollowUps = table.Column<int>(type: "int", nullable: false),
                    ClassifyReplies = table.Column<bool>(type: "bit", nullable: false),
                    RunEveryMinutes = table.Column<int>(type: "int", nullable: false),
                    LastRunUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRunSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RtrRequests",
                schema: "bench",
                columns: table => new
                {
                    RtrRequestId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AutopilotItemId = table.Column<int>(type: "int", nullable: false),
                    TokenHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ConsultantEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    Statement = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RespondedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SignedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DeclineReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RespondedIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RtrRequests", x => x.RtrRequestId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotItems_ConsultantId_RequirementId_ExternalJobId",
                schema: "bench",
                table: "AutopilotItems",
                columns: new[] { "ConsultantId", "RequirementId", "ExternalJobId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotItems_Status_OwnerUserId",
                schema: "bench",
                table: "AutopilotItems",
                columns: new[] { "Status", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_RtrRequests_AutopilotItemId",
                schema: "bench",
                table: "RtrRequests",
                column: "AutopilotItemId");

            migrationBuilder.CreateIndex(
                name: "IX_RtrRequests_TokenHash",
                schema: "bench",
                table: "RtrRequests",
                column: "TokenHash",
                unique: true);

            // Team Lead and above switch the Autopilot on/off and tune it. It starts switched OFF.
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sec.Permissions WHERE PermissionCode = 'autopilot.manage')
    INSERT sec.Permissions(PermissionCode, Description) VALUES ('autopilot.manage', N'turn the Autopilot on or off and change its settings');
INSERT sec.RolePermissions(RoleId, PermissionCode)
SELECT r.RoleId, 'autopilot.manage' FROM sec.Roles r
WHERE r.RoleId IN (1, 2, 3) AND NOT EXISTS (SELECT 1 FROM sec.RolePermissions x WHERE x.RoleId = r.RoleId AND x.PermissionCode = 'autopilot.manage');
IF NOT EXISTS (SELECT 1 FROM bench.AutopilotSettings WHERE Id = 1)
    INSERT bench.AutopilotSettings(Id, Enabled, MinScore, MaxNewPerConsultant, MaxPreparePerRun, IncludeExternalJobs, AutoFollowUp, MaxAutoFollowUps, ClassifyReplies, RunEveryMinutes)
    VALUES (1, 0, 70, 3, 20, 1, 1, 2, 1, 60);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DELETE sec.RolePermissions WHERE PermissionCode = 'autopilot.manage'; DELETE sec.Permissions WHERE PermissionCode = 'autopilot.manage';");

            migrationBuilder.DropTable(
                name: "AutopilotItems",
                schema: "bench");

            migrationBuilder.DropTable(
                name: "AutopilotSettings",
                schema: "bench");

            migrationBuilder.DropTable(
                name: "RtrRequests",
                schema: "bench");

            migrationBuilder.DropColumn(
                name: "Intent",
                schema: "bench",
                table: "VendorEmails");

            migrationBuilder.DropColumn(
                name: "IntentHandled",
                schema: "bench",
                table: "VendorEmails");

            migrationBuilder.DropColumn(
                name: "IntentSummary",
                schema: "bench",
                table: "VendorEmails");

            migrationBuilder.DropColumn(
                name: "SubmissionId",
                schema: "bench",
                table: "VendorEmails");
        }
    }
}

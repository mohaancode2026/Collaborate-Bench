using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class EmailTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailTemplates",
                schema: "bench",
                columns: table => new
                {
                    TemplateId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsShared = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<int>(type: "int", nullable: true),
                    UseCount = table.Column<int>(type: "int", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailTemplates", x => x.TemplateId);
                    table.ForeignKey(
                        name: "FK_EmailTemplates_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalSchema: "sec",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplates_IsShared_OwnerUserId",
                schema: "bench",
                table: "EmailTemplates",
                columns: new[] { "IsShared", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplates_OwnerUserId",
                schema: "bench",
                table: "EmailTemplates",
                column: "OwnerUserId");

            // Shared templates are managed by Team Lead and above; everyone can keep personal templates (mail.view).
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sec.Permissions WHERE PermissionCode = 'templates.manage')
    INSERT sec.Permissions(PermissionCode, Description) VALUES ('templates.manage', N'manage shared email templates');
INSERT sec.RolePermissions(RoleId, PermissionCode)
SELECT r.RoleId, 'templates.manage' FROM sec.Roles r
WHERE r.RoleId IN (1, 2, 3) AND NOT EXISTS (SELECT 1 FROM sec.RolePermissions x WHERE x.RoleId = r.RoleId AND x.PermissionCode = 'templates.manage');");

            // Built-in starter templates (shared, no owner). Teams can edit them or add their own.
            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM bench.EmailTemplates WHERE IsShared = 1 AND Name = N'Profile submission')
    INSERT bench.EmailTemplates(Name, Category, Subject, Body, IsShared, OwnerUserId, UseCount, CreatedUtc, UpdatedUtc)
    VALUES (N'Profile submission', N'Submission', N'Submission: {{consultant.name}} for {{requirement.title}} ({{requirement.client}})', N'Hi {{contact.firstName}},

Please find the profile of {{consultant.name}} for the {{requirement.title}} role with {{requirement.client}}.

- {{consultant.experience}}+ years as {{consultant.title}}
- Key skills: {{consultant.skills}}
- Work authorization: {{consultant.visa}}
- Location: {{consultant.location}} ({{consultant.relocation}})

Resume and RTR attached. {{consultant.firstName}} is available to interview at short notice.

Thanks,
{{me.name}}
{{me.email}}', 1, NULL, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM bench.EmailTemplates WHERE IsShared = 1 AND Name = N'Submission follow-up')
    INSERT bench.EmailTemplates(Name, Category, Subject, Body, IsShared, OwnerUserId, UseCount, CreatedUtc, UpdatedUtc)
    VALUES (N'Submission follow-up', N'Follow-up', N'Following up: {{consultant.name}} for {{requirement.title}}', N'Hi {{contact.firstName}},

Following up on {{consultant.name}}, submitted for the {{requirement.title}} role with {{requirement.client}}. Is there any feedback from the client, or an update on next steps?

Happy to set up a quick call if that helps.

Thanks,
{{me.name}}', 1, NULL, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM bench.EmailTemplates WHERE IsShared = 1 AND Name = N'Rate and details confirmation')
    INSERT bench.EmailTemplates(Name, Category, Subject, Body, IsShared, OwnerUserId, UseCount, CreatedUtc, UpdatedUtc)
    VALUES (N'Rate and details confirmation', N'Rate', N'Rate confirmation: {{requirement.title}} ({{requirement.client}})', N'Hi {{contact.firstName}},

Before I submit, could you confirm the following for the {{requirement.title}} role with {{requirement.client}}?

- Bill rate and terms ({{requirement.type}})
- Duration and start date
- Interview process
- Work setting: {{requirement.mode}}, {{requirement.location}}

Thanks,
{{me.name}}', 1, NULL, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM bench.EmailTemplates WHERE IsShared = 1 AND Name = N'Interview availability')
    INSERT bench.EmailTemplates(Name, Category, Subject, Body, IsShared, OwnerUserId, UseCount, CreatedUtc, UpdatedUtc)
    VALUES (N'Interview availability', N'Interview', N'Interview availability: {{consultant.name}} for {{requirement.title}}', N'Hi {{contact.firstName}},

Thanks for shortlisting {{consultant.name}} for the {{requirement.title}} role. {{consultant.firstName}} is available for the interview at these times (please share the time zone and meeting link):

-
-

Thanks,
{{me.name}}', 1, NULL, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM bench.EmailTemplates WHERE IsShared = 1 AND Name = N'Onboarding documents')
    INSERT bench.EmailTemplates(Name, Category, Subject, Body, IsShared, OwnerUserId, UseCount, CreatedUtc, UpdatedUtc)
    VALUES (N'Onboarding documents', N'Onboarding', N'Onboarding: {{consultant.name}} at {{requirement.client}}', N'Hi {{contact.firstName}},

For the onboarding of {{consultant.name}} with {{requirement.client}}, please share:

- Signed MSA and PO
- Background check and drug screen instructions
- Start date, reporting manager and first-day details

Thanks,
{{me.name}}', 1, NULL, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM bench.EmailTemplates WHERE IsShared = 1 AND Name = N'Bench hotlist')
    INSERT bench.EmailTemplates(Name, Category, Subject, Body, IsShared, OwnerUserId, UseCount, CreatedUtc, UpdatedUtc)
    VALUES (N'Bench hotlist', N'Hotlist', N'Available consultants - {{today}}', N'Hi {{contact.firstName}},

Sharing our consultants available now. All are ready to interview and can be submitted with RTR.

-

Please send any matching requirements my way.

Thanks,
{{me.name}}
{{me.email}}', 1, NULL, 0, SYSUTCDATETIME(), SYSUTCDATETIME());");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DELETE sec.RolePermissions WHERE PermissionCode = 'templates.manage'; DELETE sec.Permissions WHERE PermissionCode = 'templates.manage';");

            migrationBuilder.DropTable(
                name: "EmailTemplates",
                schema: "bench");
        }
    }
}

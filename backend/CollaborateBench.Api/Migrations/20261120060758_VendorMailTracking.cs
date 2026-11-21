using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class VendorMailTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailConnections",
                schema: "sec",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Mailbox = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RefreshTokenProtected = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConnectedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SyncedThroughUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailConnections", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_MailConnections_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Vendors",
                schema: "bench",
                columns: table => new
                {
                    VendorId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Domains = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vendors", x => x.VendorId);
                });

            migrationBuilder.CreateTable(
                name: "VendorEmails",
                schema: "bench",
                columns: table => new
                {
                    VendorEmailId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    VendorId = table.Column<int>(type: "int", nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ConversationId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Direction = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FromName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ToAddresses = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Preview = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Attachments = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsultantId = table.Column<int>(type: "int", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorEmails", x => x.VendorEmailId);
                    table.ForeignKey(
                        name: "FK_VendorEmails_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorEmails_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalSchema: "bench",
                        principalTable: "Vendors",
                        principalColumn: "VendorId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorEmails_UserId_MessageId",
                schema: "bench",
                table: "VendorEmails",
                columns: new[] { "UserId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorEmails_UserId_SentUtc",
                schema: "bench",
                table: "VendorEmails",
                columns: new[] { "UserId", "SentUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_VendorEmails_VendorId_SentUtc",
                schema: "bench",
                table: "VendorEmails",
                columns: new[] { "VendorId", "SentUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Vendors_Name",
                schema: "bench",
                table: "Vendors",
                column: "Name",
                unique: true);

            // New permissions: everyone sees vendor email activity (limited to their data scope); Team Lead and above manage vendors.
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sec.Permissions WHERE PermissionCode = 'mail.view')
    INSERT sec.Permissions(PermissionCode, Description) VALUES ('mail.view', N'view vendor email activity');
IF NOT EXISTS (SELECT 1 FROM sec.Permissions WHERE PermissionCode = 'vendors.manage')
    INSERT sec.Permissions(PermissionCode, Description) VALUES ('vendors.manage', N'manage vendors and their email domains');
INSERT sec.RolePermissions(RoleId, PermissionCode)
SELECT r.RoleId, p.Code FROM sec.Roles r
CROSS JOIN (VALUES ('mail.view'), ('vendors.manage')) p(Code)
WHERE (p.Code = 'mail.view' OR r.RoleId IN (1, 2, 3))
  AND NOT EXISTS (SELECT 1 FROM sec.RolePermissions x WHERE x.RoleId = r.RoleId AND x.PermissionCode = p.Code);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DELETE sec.RolePermissions WHERE PermissionCode IN ('mail.view', 'vendors.manage');
DELETE sec.Permissions WHERE PermissionCode IN ('mail.view', 'vendors.manage');");

            migrationBuilder.DropTable(
                name: "MailConnections",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "VendorEmails",
                schema: "bench");

            migrationBuilder.DropTable(
                name: "Vendors",
                schema: "bench");
        }
    }
}

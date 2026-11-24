using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class ConsultantProfileAndDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Bench assignment: Admin, Delivery Manager and Manager hand consultants to Team Leads (and see the whole bench to do it).
            // Managers also add, edit and remove consultants and upload their documents.
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sec.Permissions WHERE PermissionCode = 'bench.assign')
    INSERT sec.Permissions(PermissionCode, Description) VALUES ('bench.assign', N'assign consultants to Team Leads (sees the whole bench)');
INSERT sec.RolePermissions(RoleId, PermissionCode)
SELECT g.RoleId, g.Code FROM (VALUES
    (1, 'bench.assign'), (2, 'bench.assign'), (5, 'bench.assign'),
    (5, 'bench.create'), (5, 'bench.edit'), (5, 'bench.delete'), (5, 'parser.use')
) g(RoleId, Code)
WHERE NOT EXISTS (SELECT 1 FROM sec.RolePermissions x WHERE x.RoleId = g.RoleId AND x.PermissionCode = g.Code);");

            migrationBuilder.AddColumn<string>(
                name: "Bachelors",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentProjectEnd",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DateOfBirth",
                schema: "bench",
                table: "Consultants",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DoNotSubmit",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleVoice",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdType",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Keywords",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalName",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationConstraints",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarketingTechnology",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Masters",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PersonalPhone",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SsnLast4",
                schema: "bench",
                table: "Consultants",
                type: "varchar(4)",
                unicode: false,
                maxLength: 4,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConsultantDocuments",
                schema: "bench",
                columns: table => new
                {
                    ConsultantDocumentId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConsultantId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ExpiresOn = table.Column<DateTime>(type: "date", nullable: true),
                    UploadedByUserId = table.Column<int>(type: "int", nullable: false),
                    UploadedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsultantDocuments", x => x.ConsultantDocumentId);
                    table.ForeignKey(
                        name: "FK_ConsultantDocuments_Consultants_ConsultantId",
                        column: x => x.ConsultantId,
                        principalSchema: "bench",
                        principalTable: "Consultants",
                        principalColumn: "ConsultantId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConsultantDocuments_Users_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalSchema: "sec",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsultantDocuments_ConsultantId",
                schema: "bench",
                table: "ConsultantDocuments",
                column: "ConsultantId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsultantDocuments_UploadedByUserId",
                schema: "bench",
                table: "ConsultantDocuments",
                column: "UploadedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DELETE sec.RolePermissions WHERE PermissionCode = 'bench.assign' OR (RoleId = 5 AND PermissionCode IN ('bench.create','bench.edit','bench.delete','parser.use'));
DELETE sec.Permissions WHERE PermissionCode = 'bench.assign';");

            migrationBuilder.DropTable(
                name: "ConsultantDocuments",
                schema: "bench");

            migrationBuilder.DropColumn(
                name: "Bachelors",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "CurrentProjectEnd",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "DateOfBirth",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "DoNotSubmit",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "GoogleVoice",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "IdType",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "Keywords",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "LegalName",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "LocationConstraints",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "MarketingTechnology",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "Masters",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "PersonalPhone",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "SsnLast4",
                schema: "bench",
                table: "Consultants");
        }
    }
}

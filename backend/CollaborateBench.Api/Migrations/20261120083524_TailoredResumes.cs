using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class TailoredResumes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TailoredResumes",
                schema: "bench",
                columns: table => new
                {
                    TailoredResumeId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConsultantId = table.Column<int>(type: "int", nullable: false),
                    RequirementId = table.Column<int>(type: "int", nullable: true),
                    SourceResumeId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TargetTitle = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    TargetClient = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    JobDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DocJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Changes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Gaps = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Warnings = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Matched = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Engine = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Notice = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HideContact = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TailoredResumes", x => x.TailoredResumeId);
                    table.ForeignKey(
                        name: "FK_TailoredResumes_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "sec",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TailoredResumes_ConsultantId_UpdatedUtc",
                schema: "bench",
                table: "TailoredResumes",
                columns: new[] { "ConsultantId", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TailoredResumes_CreatedByUserId",
                schema: "bench",
                table: "TailoredResumes",
                column: "CreatedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TailoredResumes",
                schema: "bench");
        }
    }
}

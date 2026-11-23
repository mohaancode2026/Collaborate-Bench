using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class LinkedInSidePanel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                schema: "bench",
                table: "Requirements",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkedInUrl",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "bench",
                table: "Consultants",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.CreateTable(
                name: "CandidateActivities",
                schema: "bench",
                columns: table => new
                {
                    ActivityId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConsultantId = table.Column<int>(type: "int", nullable: false),
                    RequirementId = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Channel = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Outcome = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateActivities", x => x.ActivityId);
                    table.ForeignKey(
                        name: "FK_CandidateActivities_Consultants_ConsultantId",
                        column: x => x.ConsultantId,
                        principalSchema: "bench",
                        principalTable: "Consultants",
                        principalColumn: "ConsultantId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CandidateActivities_Requirements_RequirementId",
                        column: x => x.RequirementId,
                        principalSchema: "bench",
                        principalTable: "Requirements",
                        principalColumn: "RequirementId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CandidateActivities_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CandidateLeads",
                schema: "bench",
                columns: table => new
                {
                    LeadId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConsultantId = table.Column<int>(type: "int", nullable: false),
                    RequirementId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateLeads", x => x.LeadId);
                    table.ForeignKey(
                        name: "FK_CandidateLeads_Consultants_ConsultantId",
                        column: x => x.ConsultantId,
                        principalSchema: "bench",
                        principalTable: "Consultants",
                        principalColumn: "ConsultantId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CandidateLeads_Requirements_RequirementId",
                        column: x => x.RequirementId,
                        principalSchema: "bench",
                        principalTable: "Requirements",
                        principalColumn: "RequirementId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CandidateLeads_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalSchema: "sec",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CandidateReminders",
                schema: "bench",
                columns: table => new
                {
                    ReminderId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConsultantId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    DueUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Status = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateReminders", x => x.ReminderId);
                    table.ForeignKey(
                        name: "FK_CandidateReminders_Consultants_ConsultantId",
                        column: x => x.ConsultantId,
                        principalSchema: "bench",
                        principalTable: "Consultants",
                        principalColumn: "ConsultantId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CandidateReminders_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_Requirements_SourceUrl",
                schema: "bench",
                table: "Requirements",
                column: "SourceUrl",
                unique: true,
                filter: "[SourceUrl] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_Consultants_LinkedInUrl",
                schema: "bench",
                table: "Consultants",
                column: "LinkedInUrl",
                unique: true,
                filter: "[LinkedInUrl] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateActivities_ConsultantId_CreatedUtc",
                schema: "bench",
                table: "CandidateActivities",
                columns: new[] { "ConsultantId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateActivities_RequirementId",
                schema: "bench",
                table: "CandidateActivities",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateActivities_UserId",
                schema: "bench",
                table: "CandidateActivities",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateLeads_RequirementId",
                schema: "bench",
                table: "CandidateLeads",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateLeads_UpdatedByUserId",
                schema: "bench",
                table: "CandidateLeads",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "UQ_Lead_ConsReq",
                schema: "bench",
                table: "CandidateLeads",
                columns: new[] { "ConsultantId", "RequirementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandidateReminders_ConsultantId",
                schema: "bench",
                table: "CandidateReminders",
                column: "ConsultantId");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateReminders_UserId_Status_DueUtc",
                schema: "bench",
                table: "CandidateReminders",
                columns: new[] { "UserId", "Status", "DueUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CandidateActivities",
                schema: "bench");

            migrationBuilder.DropTable(
                name: "CandidateLeads",
                schema: "bench");

            migrationBuilder.DropTable(
                name: "CandidateReminders",
                schema: "bench");

            migrationBuilder.DropIndex(
                name: "UQ_Requirements_SourceUrl",
                schema: "bench",
                table: "Requirements");

            migrationBuilder.DropIndex(
                name: "UQ_Consultants_LinkedInUrl",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                schema: "bench",
                table: "Requirements");

            migrationBuilder.DropColumn(
                name: "LinkedInUrl",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "bench",
                table: "Consultants");
        }
    }
}

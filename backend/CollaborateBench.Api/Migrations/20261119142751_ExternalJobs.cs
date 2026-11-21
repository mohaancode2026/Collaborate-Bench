using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class ExternalJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalJobs",
                schema: "bench",
                columns: table => new
                {
                    ExternalJobId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Provider = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Company = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsRemote = table.Column<bool>(type: "bit", nullable: false),
                    EmploymentType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Publisher = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ApplyUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Skills = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Visas = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MinHourly = table.Column<decimal>(type: "decimal(7,2)", precision: 7, scale: 2, nullable: true),
                    MaxHourly = table.Column<decimal>(type: "decimal(7,2)", precision: 7, scale: 2, nullable: true),
                    PostedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FetchedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RequirementId = table.Column<int>(type: "int", nullable: true),
                    IsHidden = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalJobs", x => x.ExternalJobId);
                    table.ForeignKey(
                        name: "FK_ExternalJobs_Requirements_RequirementId",
                        column: x => x.RequirementId,
                        principalSchema: "bench",
                        principalTable: "Requirements",
                        principalColumn: "RequirementId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ExternalJobSearches",
                schema: "bench",
                columns: table => new
                {
                    SearchId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Provider = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    QueryKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Query = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Automatic = table.Column<bool>(type: "bit", nullable: false),
                    CacheHit = table.Column<bool>(type: "bit", nullable: false),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    ResultCount = table.Column<int>(type: "int", nullable: false),
                    JobIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalJobSearches", x => x.SearchId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalJobs_LastSeenUtc",
                schema: "bench",
                table: "ExternalJobs",
                column: "LastSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalJobs_Provider_ExternalId",
                schema: "bench",
                table: "ExternalJobs",
                columns: new[] { "Provider", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalJobs_RequirementId",
                schema: "bench",
                table: "ExternalJobs",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalJobSearches_CreatedUtc",
                schema: "bench",
                table: "ExternalJobSearches",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalJobSearches_QueryKey_CreatedUtc",
                schema: "bench",
                table: "ExternalJobSearches",
                columns: new[] { "QueryKey", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalJobs",
                schema: "bench");

            migrationBuilder.DropTable(
                name: "ExternalJobSearches",
                schema: "bench");
        }
    }
}

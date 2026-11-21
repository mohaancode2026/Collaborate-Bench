using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class FollowUpReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FollowUpReminders",
                schema: "bench",
                columns: table => new
                {
                    ReminderId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    VendorEmailId = table.Column<int>(type: "int", nullable: false),
                    ConversationKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DueUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    Auto = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowUpReminders", x => x.ReminderId);
                    table.ForeignKey(
                        name: "FK_FollowUpReminders_VendorEmails_VendorEmailId",
                        column: x => x.VendorEmailId,
                        principalSchema: "bench",
                        principalTable: "VendorEmails",
                        principalColumn: "VendorEmailId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FollowUpReminders_UserId_Status_DueUtc",
                schema: "bench",
                table: "FollowUpReminders",
                columns: new[] { "UserId", "Status", "DueUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FollowUpReminders_VendorEmailId",
                schema: "bench",
                table: "FollowUpReminders",
                column: "VendorEmailId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FollowUpReminders",
                schema: "bench");
        }
    }
}

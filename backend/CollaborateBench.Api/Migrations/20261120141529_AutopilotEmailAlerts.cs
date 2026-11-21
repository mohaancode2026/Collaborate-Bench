using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class AutopilotEmailAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EmailNotifications",
                schema: "bench",
                table: "AutopilotSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "AutopilotNotifications",
                schema: "bench",
                columns: table => new
                {
                    NotificationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    Link = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AutopilotItemId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotNotifications", x => x.NotificationId);
                });

            migrationBuilder.CreateTable(
                name: "NotificationPrefs",
                schema: "sec",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Approvals = table.Column<bool>(type: "bit", nullable: false),
                    VendorReplies = table.Column<bool>(type: "bit", nullable: false),
                    RtrUpdates = table.Column<bool>(type: "bit", nullable: false),
                    Problems = table.Column<bool>(type: "bit", nullable: false),
                    FollowUps = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPrefs", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_NotificationPrefs_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotNotifications_Status_UserId",
                schema: "bench",
                table: "AutopilotNotifications",
                columns: new[] { "Status", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutopilotNotifications",
                schema: "bench");

            migrationBuilder.DropTable(
                name: "NotificationPrefs",
                schema: "sec");

            migrationBuilder.DropColumn(
                name: "EmailNotifications",
                schema: "bench",
                table: "AutopilotSettings");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class AiInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InboxItems",
                schema: "bench",
                columns: table => new
                {
                    InboxItemId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ConversationId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    FromAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FromName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    HasAttachments = table.Column<bool>(type: "bit", nullable: false),
                    Category = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Priority = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    PriorityReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    JobJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MatchesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NeedsReply = table.Column<bool>(type: "bit", nullable: false),
                    Engine = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    RequirementId = table.Column<int>(type: "int", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxItems", x => x.InboxItemId);
                    table.ForeignKey(
                        name: "FK_InboxItems_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InboxTasks",
                schema: "bench",
                columns: table => new
                {
                    InboxTaskId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    InboxItemId = table.Column<int>(type: "int", nullable: true),
                    Kind = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    DueUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationMinutes = table.Column<int>(type: "int", nullable: true),
                    ConsultantId = table.Column<int>(type: "int", nullable: true),
                    Location = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SharedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    Auto = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxTasks", x => x.InboxTaskId);
                    table.ForeignKey(
                        name: "FK_InboxTasks_InboxItems_InboxItemId",
                        column: x => x.InboxItemId,
                        principalSchema: "bench",
                        principalTable: "InboxItems",
                        principalColumn: "InboxItemId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_UserId_MessageId",
                schema: "bench",
                table: "InboxItems",
                columns: new[] { "UserId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_UserId_Status_ReceivedUtc",
                schema: "bench",
                table: "InboxItems",
                columns: new[] { "UserId", "Status", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxTasks_InboxItemId",
                schema: "bench",
                table: "InboxTasks",
                column: "InboxItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxTasks_UserId_Status_DueUtc",
                schema: "bench",
                table: "InboxTasks",
                columns: new[] { "UserId", "Status", "DueUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxTasks",
                schema: "bench");

            migrationBuilder.DropTable(
                name: "InboxItems",
                schema: "bench");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class InboxWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Action",
                schema: "bench",
                table: "InboxTasks",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InterviewType",
                schema: "bench",
                table: "InboxTasks",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MockSession",
                schema: "bench",
                table: "InboxTasks",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ParentTaskId",
                schema: "bench",
                table: "InboxTasks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RepeatBusinessDays",
                schema: "bench",
                table: "InboxTasks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Sequence",
                schema: "bench",
                table: "InboxTasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SlotsJson",
                schema: "bench",
                table: "InboxTasks",
                type: "varchar(400)",
                unicode: false,
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConsultantId",
                schema: "bench",
                table: "InboxItems",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestsJson",
                schema: "bench",
                table: "InboxItems",
                type: "nvarchar(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlotsJson",
                schema: "bench",
                table: "InboxItems",
                type: "varchar(400)",
                unicode: false,
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Team",
                schema: "bench",
                table: "InboxItems",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxTasks_ParentTaskId",
                schema: "bench",
                table: "InboxTasks",
                column: "ParentTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboxTasks_ParentTaskId",
                schema: "bench",
                table: "InboxTasks");

            migrationBuilder.DropColumn(
                name: "Action",
                schema: "bench",
                table: "InboxTasks");

            migrationBuilder.DropColumn(
                name: "InterviewType",
                schema: "bench",
                table: "InboxTasks");

            migrationBuilder.DropColumn(
                name: "MockSession",
                schema: "bench",
                table: "InboxTasks");

            migrationBuilder.DropColumn(
                name: "ParentTaskId",
                schema: "bench",
                table: "InboxTasks");

            migrationBuilder.DropColumn(
                name: "RepeatBusinessDays",
                schema: "bench",
                table: "InboxTasks");

            migrationBuilder.DropColumn(
                name: "Sequence",
                schema: "bench",
                table: "InboxTasks");

            migrationBuilder.DropColumn(
                name: "SlotsJson",
                schema: "bench",
                table: "InboxTasks");

            migrationBuilder.DropColumn(
                name: "ConsultantId",
                schema: "bench",
                table: "InboxItems");

            migrationBuilder.DropColumn(
                name: "RequestsJson",
                schema: "bench",
                table: "InboxItems");

            migrationBuilder.DropColumn(
                name: "SlotsJson",
                schema: "bench",
                table: "InboxItems");

            migrationBuilder.DropColumn(
                name: "Team",
                schema: "bench",
                table: "InboxItems");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class GridIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Submissions_OwnerUserId",
                schema: "bench",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_UserId",
                schema: "sec",
                table: "AuditLog");

            migrationBuilder.AlterColumn<string>(
                name: "Stage",
                schema: "bench",
                table: "Submissions",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "Outcome",
                schema: "sec",
                table: "AuditLog",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_OwnerUserId_StageChangedUtc",
                schema: "bench",
                table: "Submissions",
                columns: new[] { "OwnerUserId", "StageChangedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_Stage_StageChangedUtc",
                schema: "bench",
                table: "Submissions",
                columns: new[] { "Stage", "StageChangedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Outcome_AuditId",
                schema: "sec",
                table: "AuditLog",
                columns: new[] { "Outcome", "AuditId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_UserId_AuditId",
                schema: "sec",
                table: "AuditLog",
                columns: new[] { "UserId", "AuditId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Submissions_OwnerUserId_StageChangedUtc",
                schema: "bench",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_Stage_StageChangedUtc",
                schema: "bench",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_Outcome_AuditId",
                schema: "sec",
                table: "AuditLog");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_UserId_AuditId",
                schema: "sec",
                table: "AuditLog");

            migrationBuilder.AlterColumn<string>(
                name: "Stage",
                schema: "bench",
                table: "Submissions",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "Outcome",
                schema: "sec",
                table: "AuditLog",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_OwnerUserId",
                schema: "bench",
                table: "Submissions",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_UserId",
                schema: "sec",
                table: "AuditLog",
                column: "UserId");
        }
    }
}

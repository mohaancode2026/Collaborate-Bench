using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class SubmissionUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Submissions_ConsultantId",
                schema: "bench",
                table: "Submissions");

            migrationBuilder.CreateIndex(
                name: "UQ_Sub_ConsReq",
                schema: "bench",
                table: "Submissions",
                columns: new[] { "ConsultantId", "RequirementId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_Sub_ConsReq",
                schema: "bench",
                table: "Submissions");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_ConsultantId",
                schema: "bench",
                table: "Submissions",
                column: "ConsultantId");
        }
    }
}

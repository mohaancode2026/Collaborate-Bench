using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class InboxAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttachmentsJson",
                schema: "bench",
                table: "InboxItems",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttachmentsJson",
                schema: "bench",
                table: "InboxItems");
        }
    }
}

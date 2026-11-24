using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class InboxSentMail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Direction",
                schema: "bench",
                table: "InboxItems",
                type: "varchar(3)",
                unicode: false,
                maxLength: 3,
                nullable: false,
                defaultValue: "In");

            migrationBuilder.AddColumn<string>(
                name: "ToAddresses",
                schema: "bench",
                table: "InboxItems",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_UserId_ConversationId",
                schema: "bench",
                table: "InboxItems",
                columns: new[] { "UserId", "ConversationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboxItems_UserId_ConversationId",
                schema: "bench",
                table: "InboxItems");

            migrationBuilder.DropColumn(
                name: "Direction",
                schema: "bench",
                table: "InboxItems");

            migrationBuilder.DropColumn(
                name: "ToAddresses",
                schema: "bench",
                table: "InboxItems");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <inheritdoc />
    public partial class ConsultantVendorDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Availability",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Certifications",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Education",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PassportLast4",
                schema: "bench",
                table: "Consultants",
                type: "varchar(4)",
                unicode: false,
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PassportProtected",
                schema: "bench",
                table: "Consultants",
                type: "varchar(600)",
                unicode: false,
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredLocations",
                schema: "bench",
                table: "Consultants",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "UsExperienceYears",
                schema: "bench",
                table: "Consultants",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VisaValidUntil",
                schema: "bench",
                table: "Consultants",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Availability",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "Certifications",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "Education",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "PassportLast4",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "PassportProtected",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "PreferredLocations",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "UsExperienceYears",
                schema: "bench",
                table: "Consultants");

            migrationBuilder.DropColumn(
                name: "VisaValidUntil",
                schema: "bench",
                table: "Consultants");
        }
    }
}

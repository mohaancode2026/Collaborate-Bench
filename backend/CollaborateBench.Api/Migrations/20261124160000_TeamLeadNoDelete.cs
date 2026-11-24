using CollaborateBench.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <summary>Team Leads assign consultants to recruiters but don't delete consultants or their documents
    /// (Admin, Delivery Manager and Manager do). Data only, so there is no model change.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261124160000_TeamLeadNoDelete")]
    public partial class TeamLeadNoDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE sec.RolePermissions WHERE RoleId = 3 AND PermissionCode = 'bench.delete';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sec.RolePermissions WHERE RoleId = 3 AND PermissionCode = 'bench.delete')
    INSERT sec.RolePermissions(RoleId, PermissionCode) VALUES (3, 'bench.delete');");
        }
    }
}

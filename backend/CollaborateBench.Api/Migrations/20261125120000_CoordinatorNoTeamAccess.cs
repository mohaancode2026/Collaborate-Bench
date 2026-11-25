using CollaborateBench.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <summary>Technical Coordinators (Manager, role 5) don't see Team &amp; Access. Data only, so there is no model change.
    /// Takes effect at the user's next sign-in.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261125120000_CoordinatorNoTeamAccess")]
    public partial class CoordinatorNoTeamAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE sec.RolePermissions WHERE RoleId = 5 AND PermissionCode = 'team.view';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sec.RolePermissions WHERE RoleId = 5 AND PermissionCode = 'team.view')
    INSERT sec.RolePermissions(RoleId, PermissionCode) VALUES (5, 'team.view');");
        }
    }
}

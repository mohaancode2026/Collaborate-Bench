using CollaborateBench.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <summary>
    /// Role hierarchy: Admin and Delivery Manager see everything; a Manager sends the hotlist (no wider visibility);
    /// a Team Lead views leads and assigns consultants to recruiters; a Recruiter works the consultants assigned to them.
    /// Data only, so there is no model change.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261124120000_ManagerRoleAndPermissions")]
    public partial class ManagerRoleAndPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sec.Roles WHERE RoleId = 5)
    INSERT sec.Roles(RoleId, Name, DataScope, Rank) VALUES (5, N'Manager', 'TEAM', 70);

IF NOT EXISTS (SELECT 1 FROM sec.Permissions WHERE PermissionCode = 'hotlist.send')
    INSERT sec.Permissions(PermissionCode, Description) VALUES ('hotlist.send', N'generate and send the hotlist to vendors');
IF NOT EXISTS (SELECT 1 FROM sec.Permissions WHERE PermissionCode = 'leads.view')
    INSERT sec.Permissions(PermissionCode, Description) VALUES ('leads.view', N'view sourced leads');

-- Admin 1, Delivery Manager 2, Team Lead 3, Recruiter 4, Manager 5
INSERT sec.RolePermissions(RoleId, PermissionCode)
SELECT g.RoleId, g.Code FROM (VALUES
    (1, 'hotlist.send'), (2, 'hotlist.send'), (5, 'hotlist.send'),
    (1, 'leads.view'), (2, 'leads.view'), (3, 'leads.view'),
    (5, 'dash.view'), (5, 'bench.view'), (5, 'mail.view'), (5, 'team.view'), (5, 'ai.copilot')
) g(RoleId, Code)
WHERE NOT EXISTS (SELECT 1 FROM sec.RolePermissions x WHERE x.RoleId = g.RoleId AND x.PermissionCode = g.Code);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DELETE sec.RolePermissions WHERE PermissionCode IN ('hotlist.send', 'leads.view') OR RoleId = 5;
DELETE sec.Permissions WHERE PermissionCode IN ('hotlist.send', 'leads.view');
UPDATE sec.Users SET RoleId = 4 WHERE RoleId = 5;
DELETE sec.Roles WHERE RoleId = 5;");
        }
    }
}

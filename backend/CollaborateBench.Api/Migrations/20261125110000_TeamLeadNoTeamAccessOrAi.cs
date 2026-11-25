using CollaborateBench.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <summary>Team Leads (role 3) see their team's consultants and counts, but not Team &amp; Access, the AI Resume Parser,
    /// the AI Resume Builder or Add consultant. Data only, so there is no model change. Takes effect at the user's next sign-in.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261125110000_TeamLeadNoTeamAccessOrAi")]
    public partial class TeamLeadNoTeamAccessOrAi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE sec.RolePermissions WHERE RoleId = 3 AND PermissionCode IN ('team.view', 'parser.use', 'bench.create');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"INSERT sec.RolePermissions(RoleId, PermissionCode)
SELECT 3, g.Code FROM (VALUES ('team.view'), ('parser.use'), ('bench.create')) g(Code)
WHERE NOT EXISTS (SELECT 1 FROM sec.RolePermissions x WHERE x.RoleId = 3 AND x.PermissionCode = g.Code);");
        }
    }
}

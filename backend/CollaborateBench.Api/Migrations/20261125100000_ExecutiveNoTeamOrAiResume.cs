using CollaborateBench.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <summary>Bench Sales Executives (Recruiter, role 4) don't see Team &amp; Access, the AI Resume Parser or the AI Resume Builder.
    /// Data only, so there is no model change. Takes effect for a user at their next sign-in (permissions travel in the token).</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261125100000_ExecutiveNoTeamOrAiResume")]
    public partial class ExecutiveNoTeamOrAiResume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE sec.RolePermissions WHERE RoleId = 4 AND PermissionCode IN ('team.view', 'parser.use');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"INSERT sec.RolePermissions(RoleId, PermissionCode)
SELECT 4, g.Code FROM (VALUES ('team.view'), ('parser.use')) g(Code)
WHERE NOT EXISTS (SELECT 1 FROM sec.RolePermissions x WHERE x.RoleId = 4 AND x.PermissionCode = g.Code);");
        }
    }
}

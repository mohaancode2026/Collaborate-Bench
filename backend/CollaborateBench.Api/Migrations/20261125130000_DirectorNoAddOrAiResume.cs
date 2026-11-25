using CollaborateBench.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollaborateBench.Api.Migrations
{
    /// <summary>Directors of Sales (Delivery Manager, role 2) oversee every team and manage users, but don't add consultants or use
    /// the AI Resume Parser / Resume Builder. Data only, so there is no model change. Takes effect at the user's next sign-in.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261125130000_DirectorNoAddOrAiResume")]
    public partial class DirectorNoAddOrAiResume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE sec.RolePermissions WHERE RoleId = 2 AND PermissionCode IN ('parser.use', 'bench.create');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"INSERT sec.RolePermissions(RoleId, PermissionCode)
SELECT 2, g.Code FROM (VALUES ('parser.use'), ('bench.create')) g(Code)
WHERE NOT EXISTS (SELECT 1 FROM sec.RolePermissions x WHERE x.RoleId = 2 AND x.PermissionCode = g.Code);");
        }
    }
}

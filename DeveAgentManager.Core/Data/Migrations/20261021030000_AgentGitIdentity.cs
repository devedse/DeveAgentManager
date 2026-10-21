using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DeveAgentManager.Core.Data.Migrations;

[DbContext(typeof(ManagerDbContext))]
[Migration("20261021030000_AgentGitIdentity")]
public sealed class AgentGitIdentity : Migration
{
    // Null keeps every existing agent on its owner's identity, including harnesses that are running now.
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "GitIdentityUserId", table: "Agents", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "RunningGitIdentityUserId", table: "Agents", type: "TEXT", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "GitIdentityUserId", table: "Agents");
        migrationBuilder.DropColumn(name: "RunningGitIdentityUserId", table: "Agents");
    }
}

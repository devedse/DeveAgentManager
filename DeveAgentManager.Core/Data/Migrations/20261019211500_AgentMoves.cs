using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DeveAgentManager.Core.Data.Migrations;

[DbContext(typeof(ManagerDbContext))]
[Migration("20261019211500_AgentMoves")]
public sealed class AgentMoves : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "MoveJson", table: "Agents", type: "TEXT", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "MoveJson", table: "Agents");
}

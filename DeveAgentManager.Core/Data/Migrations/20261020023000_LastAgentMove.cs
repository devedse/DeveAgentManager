using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DeveAgentManager.Core.Data.Migrations;

[DbContext(typeof(ManagerDbContext))]
[Migration("20261020023000_LastAgentMove")]
public sealed class LastAgentMove : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "LastMoveJson", table: "Agents", type: "TEXT", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "LastMoveJson", table: "Agents");
}

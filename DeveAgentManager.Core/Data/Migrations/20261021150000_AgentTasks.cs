using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DeveAgentManager.Core.Data.Migrations;

[DbContext(typeof(ManagerDbContext))]
[Migration("20261021150000_AgentTasks")]
public sealed class AgentTasks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "TasksJson", table: "Agents", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<long>(name: "TasksRevision", table: "Agents", type: "INTEGER", nullable: false, defaultValue: 0L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "TasksJson", table: "Agents");
        migrationBuilder.DropColumn(name: "TasksRevision", table: "Agents");
    }
}

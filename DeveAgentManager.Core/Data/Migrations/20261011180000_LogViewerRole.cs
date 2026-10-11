using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace DeveAgentManager.Core.Data.Migrations;
public partial class LogViewerRole : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("UPDATE Users SET Roles = Roles | 32 WHERE Roles = 31;");
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("UPDATE Users SET Roles = Roles & ~32;");
}

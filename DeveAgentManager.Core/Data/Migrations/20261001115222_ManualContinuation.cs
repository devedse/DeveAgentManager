using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveAgentManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManualContinuation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Manual",
                table: "AgentLimits",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Manual",
                table: "AgentLimits");
        }
    }
}

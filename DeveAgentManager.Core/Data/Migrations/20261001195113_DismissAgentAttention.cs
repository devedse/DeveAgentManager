using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveAgentManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class DismissAgentAttention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DismissedAttentionToken",
                table: "Agents",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DismissedAttentionToken",
                table: "Agents");
        }
    }
}

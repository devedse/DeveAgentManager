using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveAgentManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentActivityAndAttention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActivityJson",
                table: "Agents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SeenCompletionSequence",
                table: "Agents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityJson",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "SeenCompletionSequence",
                table: "Agents");
        }
    }
}

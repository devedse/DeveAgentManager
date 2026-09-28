using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveAgentManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentIcons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "IconVersion",
                table: "Agents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentIcons",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ChosenByUser = table.Column<bool>(type: "INTEGER", nullable: false),
                    CheckedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentIcons", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_AgentIcons_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentIcons");

            migrationBuilder.DropColumn(
                name: "IconVersion",
                table: "Agents");
        }
    }
}

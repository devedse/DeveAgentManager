using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveAgentManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_AgentId",
                table: "Sessions");

            migrationBuilder.AddColumn<long>(
                name: "SavedSize",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "TitlePinned",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "TotalSize",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceAgentId",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            // Preserve ownership of every old conversation, then select only the newest if a prior race created duplicates.
            migrationBuilder.Sql("UPDATE Sessions SET WorkspaceAgentId = AgentId;");
            migrationBuilder.Sql("""
                UPDATE Sessions SET AgentId = NULL WHERE AgentId IS NOT NULL AND Id NOT IN
                (SELECT Id FROM (SELECT Id, ROW_NUMBER() OVER (PARTITION BY AgentId ORDER BY UpdatedAt DESC, Id DESC) AS n
                 FROM Sessions WHERE AgentId IS NOT NULL) WHERE n = 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_AgentId",
                table: "Sessions",
                column: "AgentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_WorkspaceAgentId",
                table: "Sessions",
                column: "WorkspaceAgentId");

            migrationBuilder.AddForeignKey(
                name: "FK_Sessions_Agents_WorkspaceAgentId",
                table: "Sessions",
                column: "WorkspaceAgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sessions_Agents_WorkspaceAgentId",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_AgentId",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_WorkspaceAgentId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "SavedSize",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "TitlePinned",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "TotalSize",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "WorkspaceAgentId",
                table: "Sessions");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_AgentId",
                table: "Sessions",
                column: "AgentId");
        }
    }
}

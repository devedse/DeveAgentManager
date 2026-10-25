using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveAgentManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceClones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsWorktree",
                table: "Agents",
                newName: "IsClone");

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeptFoldersJson",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ProjectId",
                table: "Sessions",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_Sessions_Projects_ProjectId",
                table: "Sessions",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Conversations belong to their workspace's project, so they stay with it when a workspace is removed.
            migrationBuilder.Sql("UPDATE Sessions SET ProjectId = (SELECT ProjectId FROM Agents WHERE Agents.Id = COALESCE(Sessions.WorkspaceAgentId, Sessions.AgentId)) WHERE COALESCE(WorkspaceAgentId, AgentId) IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sessions_Projects_ProjectId",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_ProjectId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "KeptFoldersJson",
                table: "Projects");

            migrationBuilder.RenameColumn(
                name: "IsClone",
                table: "Agents",
                newName: "IsWorktree");
        }
    }
}

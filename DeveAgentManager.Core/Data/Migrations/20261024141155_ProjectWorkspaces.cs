using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveAgentManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProjectWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsWorktree",
                table: "Agents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "Agents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceCreationJson",
                table: "Agents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    RootDirectory = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_Runners_RunnerId",
                        column: x => x.RunnerId,
                        principalTable: "Runners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Projects_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_ProjectId",
                table: "Agents",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_OwnerId",
                table: "Projects",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_RunnerId",
                table: "Projects",
                column: "RunnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_Projects_ProjectId",
                table: "Agents",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // One project per existing agent; a matching remote URL never implies shared ownership or storage.
            migrationBuilder.Sql("INSERT INTO Projects (Id, RunnerId, OwnerId, Name, RootDirectory, CreatedAt) SELECT Id, RunnerId, OwnerId, Name, WorkingDirectory, CreatedAt FROM Agents");
            migrationBuilder.Sql("UPDATE Agents SET ProjectId = Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite rebuilds Agents at the end; detach references before dropping Projects.
            migrationBuilder.Sql("UPDATE Agents SET ProjectId = NULL");
            migrationBuilder.DropForeignKey(
                name: "FK_Agents_Projects_ProjectId",
                table: "Agents");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Agents_ProjectId",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "IsWorktree",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "WorkspaceCreationJson",
                table: "Agents");
        }
    }
}

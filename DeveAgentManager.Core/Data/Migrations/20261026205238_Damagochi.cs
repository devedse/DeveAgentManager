using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveAgentManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Damagochi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PetInputClaims",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PetInputClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PetInputClaims_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PetUsageCredits",
                columns: table => new
                {
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    PromptId = table.Column<string>(type: "TEXT", nullable: false),
                    Tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PetUsageCredits", x => new { x.Source, x.PromptId });
                    table.ForeignKey(
                        name: "FK_PetUsageCredits_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "UserPets",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Species = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Experience = table.Column<long>(type: "INTEGER", nullable: false),
                    Happiness = table.Column<int>(type: "INTEGER", nullable: false),
                    SnoozedUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReducedMotion = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Reaction = table.Column<string>(type: "TEXT", nullable: false),
                    ReactionAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastInteractionAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPets", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserPets_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("UPDATE Users SET Roles = Roles | 128 WHERE Roles = 127;");


            migrationBuilder.CreateIndex(
                name: "IX_PetInputClaims_SessionId_At",
                table: "PetInputClaims",
                columns: new[] { "SessionId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PetInputClaims_UserId",
                table: "PetInputClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PetUsageCredits_UserId",
                table: "PetUsageCredits",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Users SET Roles = Roles & ~128;");
            migrationBuilder.DropTable(
                name: "PetInputClaims");

            migrationBuilder.DropTable(
                name: "PetUsageCredits");

            migrationBuilder.DropTable(
                name: "UserPets");

        }
    }
}

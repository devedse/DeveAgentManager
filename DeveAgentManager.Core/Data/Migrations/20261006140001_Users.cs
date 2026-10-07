using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveAgentManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Users : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Everything from before users existed belongs to the first user (User.BootstrapId), inserted below. The Manager
            // gives it its username and password from ADMIN_USERNAME and ADMIN_PASSWORD at start-up.
            migrationBuilder.DropIndex(
                name: "IX_Accounts_HarnessId",
                table: "Accounts");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "Sessions",
                type: "TEXT",
                nullable: false,
                defaultValue: User.BootstrapId);

            migrationBuilder.AddColumn<bool>(
                name: "KeysOnly",
                table: "Runners",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "Runners",
                type: "TEXT",
                nullable: false,
                defaultValue: User.BootstrapId);

            migrationBuilder.AddColumn<DateTime>(
                name: "PasswordChangedAt",
                table: "Runners",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "Agents",
                type: "TEXT",
                nullable: false,
                defaultValue: User.BootstrapId);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "Accounts",
                type: "TEXT",
                nullable: false,
                defaultValue: User.BootstrapId);

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ActorName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Detail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: false),
                    Roles = table.Column<int>(type: "INTEGER", nullable: false),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailedSignIns = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSignInAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentShares",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Access = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    GrantedById = table.Column<Guid>(type: "TEXT", nullable: true),
                    GrantedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentShares", x => new { x.AgentId, x.UserId });
                    table.ForeignKey(
                        name: "FK_AgentShares_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentShares_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunnerMembers",
                columns: table => new
                {
                    RunnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Access = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    GrantedById = table.Column<Guid>(type: "TEXT", nullable: true),
                    GrantedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunnerMembers", x => new { x.RunnerId, x.UserId });
                    table.ForeignKey(
                        name: "FK_RunnerMembers_Runners_RunnerId",
                        column: x => x.RunnerId,
                        principalTable: "Runners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RunnerMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionShares",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GrantedById = table.Column<Guid>(type: "TEXT", nullable: true),
                    GrantedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionShares", x => new { x.SessionId, x.UserId });
                    table.ForeignKey(
                        name: "FK_SessionShares_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SessionShares_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSettings",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSettings", x => new { x.UserId, x.Key });
                    table.ForeignKey(
                        name: "FK_UserSettings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "CreatedAt", "Disabled", "DisplayName", "FailedSignIns", "LastSignInAt", "LockedUntil", "MustChangePassword", "PasswordHash", "Roles", "SecurityStamp", "UpdatedAt", "Username" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), new DateTime(2026, 10, 6, 0, 0, 0, 0, DateTimeKind.Utc), false, "Admin", 0, null, null, false, "", 15, "bootstrap", new DateTime(2026, 10, 6, 0, 0, 0, 0, DateTimeKind.Utc), "admin" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_OwnerId",
                table: "Sessions",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Runners_OwnerId",
                table: "Runners",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_OwnerId",
                table: "Agents",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_OwnerId_HarnessId",
                table: "Accounts",
                columns: new[] { "OwnerId", "HarnessId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentShares_UserId",
                table: "AgentShares",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_At",
                table: "AuditEvents",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_RunnerMembers_UserId",
                table: "RunnerMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionShares_UserId",
                table: "SessionShares",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Accounts_Users_OwnerId",
                table: "Accounts",
                column: "OwnerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_Users_OwnerId",
                table: "Agents",
                column: "OwnerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Runners_Users_OwnerId",
                table: "Runners",
                column: "OwnerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Sessions_Users_OwnerId",
                table: "Sessions",
                column: "OwnerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Accounts_Users_OwnerId",
                table: "Accounts");

            migrationBuilder.DropForeignKey(
                name: "FK_Agents_Users_OwnerId",
                table: "Agents");

            migrationBuilder.DropForeignKey(
                name: "FK_Runners_Users_OwnerId",
                table: "Runners");

            migrationBuilder.DropForeignKey(
                name: "FK_Sessions_Users_OwnerId",
                table: "Sessions");

            migrationBuilder.DropTable(
                name: "AgentShares");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "RunnerMembers");

            migrationBuilder.DropTable(
                name: "SessionShares");

            migrationBuilder.DropTable(
                name: "UserSettings");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_OwnerId",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Runners_OwnerId",
                table: "Runners");

            migrationBuilder.DropIndex(
                name: "IX_Agents_OwnerId",
                table: "Agents");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_OwnerId_HarnessId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "KeysOnly",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "PasswordChangedAt",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Accounts");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_HarnessId",
                table: "Accounts",
                column: "HarnessId");
        }
    }
}

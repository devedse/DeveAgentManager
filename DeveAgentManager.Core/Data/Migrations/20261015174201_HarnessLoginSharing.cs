using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveAgentManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class HarnessLoginSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE TEMP TABLE LoginMigration AS SELECT Id, OwnerId, IsDefault FROM Accounts;");
            migrationBuilder.DropForeignKey(
                name: "FK_Accounts_Users_OwnerId",
                table: "Accounts");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_OwnerId_HarnessId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Accounts");

            migrationBuilder.RenameColumn(
                name: "IsDefault",
                table: "Accounts",
                newName: "Enabled");

            migrationBuilder.AddColumn<bool>(
                name: "LoginBlocked",
                table: "Agents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "LoginStopPending",
                table: "Agents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "RunningAccountId",
                table: "Agents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AccessVersion",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "Accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountCleanups",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunnerId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountCleanups", x => new { x.AccountId, x.RunnerId });
                    table.ForeignKey(
                        name: "FK_AccountCleanups_Runners_RunnerId",
                        column: x => x.RunnerId,
                        principalTable: "Runners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccountGrants",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Access = table.Column<int>(type: "INTEGER", nullable: false),
                    GrantedById = table.Column<Guid>(type: "TEXT", nullable: false),
                    GrantedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountGrants", x => new { x.AccountId, x.UserId });
                    table.ForeignKey(
                        name: "FK_AccountGrants_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountGrants_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentLoginGrants",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentLoginGrants", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_AgentLoginGrants_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentLoginGrants_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserHarnessDefaults",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HarnessId = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserHarnessDefaults", x => new { x.UserId, x.HarnessId });
                    table.ForeignKey(
                        name: "FK_UserHarnessDefaults_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserHarnessDefaults_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                UPDATE Users SET Roles = Roles | 64 WHERE Roles = 63;
                UPDATE Accounts SET Enabled = 1, AccessVersion = 1,
                    CreatedById = (SELECT OwnerId FROM LoginMigration WHERE LoginMigration.Id = Accounts.Id);
                INSERT INTO AccountGrants(AccountId, UserId, Access, GrantedById, GrantedAt)
                    SELECT a.Id, m.OwnerId, 2, m.OwnerId, a.CreatedAt FROM Accounts a JOIN LoginMigration m ON m.Id = a.Id;
                INSERT INTO UserHarnessDefaults(UserId, HarnessId, AccountId)
                    SELECT m.OwnerId, a.HarnessId, a.Id FROM Accounts a JOIN LoginMigration m ON m.Id = a.Id WHERE m.IsDefault = 1;
                INSERT INTO AgentLoginGrants(AgentId, AccountId, OwnerId)
                    SELECT a.Id, a.AccountId, a.OwnerId FROM Agents a JOIN LoginMigration m ON m.Id = a.AccountId WHERE m.OwnerId != a.OwnerId;
                UPDATE Agents SET RunningAccountId = COALESCE(AccountId,
                    (SELECT p.AccountId FROM UserHarnessDefaults p WHERE p.UserId = Agents.OwnerId AND p.HarnessId = Agents.HarnessId))
                    WHERE State IN ('Running', 'Starting');
                DROP TABLE LoginMigration;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_CreatedById",
                table: "Accounts",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_HarnessId",
                table: "Accounts",
                column: "HarnessId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountCleanups_RunnerId",
                table: "AccountCleanups",
                column: "RunnerId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountGrants_UserId",
                table: "AccountGrants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentLoginGrants_AccountId",
                table: "AgentLoginGrants",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_UserHarnessDefaults_AccountId",
                table: "UserHarnessDefaults",
                column: "AccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_Accounts_Users_CreatedById",
                table: "Accounts",
                column: "CreatedById",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Harness login sharing cannot be represented by the old ownership model. Restore a compatible database backup when rolling back.");
    }
}

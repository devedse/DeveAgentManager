using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveAgentManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Runners",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: false),
                    TemplateId = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MachineId = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderData = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StateMessage = table.Column<string>(type: "TEXT", nullable: true),
                    Host = table.Column<string>(type: "TEXT", nullable: true),
                    SshPort = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedPassword = table.Column<string>(type: "TEXT", nullable: true),
                    HostKeyFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    HomeDirectory = table.Column<string>(type: "TEXT", nullable: true),
                    Cores = table.Column<int>(type: "INTEGER", nullable: false),
                    MemoryMb = table.Column<int>(type: "INTEGER", nullable: false),
                    DiskGb = table.Column<int>(type: "INTEGER", nullable: false),
                    Harnesses = table.Column<string>(type: "TEXT", nullable: false),
                    SetupVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    InstalledVersions = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSetupAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runners", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    HarnessId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkingDirectory = table.Column<string>(type: "TEXT", nullable: false),
                    RepositoryUrl = table.Column<string>(type: "TEXT", nullable: true),
                    RepositoryBranch = table.Column<string>(type: "TEXT", nullable: true),
                    TmuxSession = table.Column<string>(type: "TEXT", nullable: false),
                    HarnessSessionId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StateMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Agents_Runners_RunnerId",
                        column: x => x.RunnerId,
                        principalTable: "Runners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_RunnerId_Name",
                table: "Agents",
                columns: new[] { "RunnerId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Runners_Name",
                table: "Runners",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Runners");
        }
    }
}

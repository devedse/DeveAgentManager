using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveAgentManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PetLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Generation",
                table: "UserPets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LifetimeTokens",
                table: "UserPets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Generation",
                table: "PetUsageCredits",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Generation",
                table: "PetInputClaims",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Existing pet progress is retained under the slower curve; establish its lifetime baseline.
            migrationBuilder.Sql("UPDATE UserPets SET LifetimeTokens = Experience;");
            // Untouched disabled defaults become unchosen eggs. Active/named/progressed pets are preserved.
            migrationBuilder.Sql("UPDATE UserPets SET Species = '', Name = '' WHERE Enabled = 0 AND Experience = 0 AND ((Species = 'snek' AND Name = 'Snek') OR (Species = 'silkie' AND Name = 'Byte'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Generation",
                table: "UserPets");

            migrationBuilder.DropColumn(
                name: "LifetimeTokens",
                table: "UserPets");

            migrationBuilder.DropColumn(
                name: "Generation",
                table: "PetUsageCredits");

            migrationBuilder.DropColumn(
                name: "Generation",
                table: "PetInputClaims");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveAgentManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConversationReader : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Upgrade every old Admin, without restoring roles to a bootstrap user who was demoted.
            migrationBuilder.Sql("UPDATE Users SET Roles = Roles | 16, SecurityStamp = lower(hex(randomblob(16))) WHERE Roles = 15;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Users SET Roles = Roles & ~16, SecurityStamp = lower(hex(randomblob(16))) WHERE (Roles & 16) != 0;");
        }
    }
}

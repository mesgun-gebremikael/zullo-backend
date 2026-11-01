using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zullo.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStandaloneActivityPrivacy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActivityPrivacyUntilUtc",
                table: "CommerceAccounts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityPrivacyUntilUtc",
                table: "CommerceAccounts");
        }
    }
}

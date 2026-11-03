using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zullo.Api.Migrations
{
    /// <inheritdoc />
    public partial class DiscoveryLocationAndPrompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DiscoveryAgeConfigured",
                table: "User",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DiscoveryMaxAge",
                table: "User",
                type: "integer",
                nullable: false,
                defaultValue: 99);

            migrationBuilder.AddColumn<int>(
                name: "DiscoveryMinAge",
                table: "User",
                type: "integer",
                nullable: false,
                defaultValue: 18);

            migrationBuilder.AddColumn<bool>(
                name: "ExpandAge",
                table: "User",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ExpandDistance",
                table: "User",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutomaticLocation",
                table: "Profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DiscoveryLocationName",
                table: "Profiles",
                type: "character varying(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "LocationUpdatedAtUtc",
                table: "Profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptAnswer",
                table: "Profiles",
                type: "character varying(250)",
                maxLength: 250,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PromptKey",
                table: "Profiles",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_IsVisible_Lat",
                table: "Profiles",
                columns: new[] { "IsVisible", "Lat" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Profiles_IsVisible_Lat",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "DiscoveryAgeConfigured",
                table: "User");

            migrationBuilder.DropColumn(
                name: "DiscoveryMaxAge",
                table: "User");

            migrationBuilder.DropColumn(
                name: "DiscoveryMinAge",
                table: "User");

            migrationBuilder.DropColumn(
                name: "ExpandAge",
                table: "User");

            migrationBuilder.DropColumn(
                name: "ExpandDistance",
                table: "User");

            migrationBuilder.DropColumn(
                name: "AutomaticLocation",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "DiscoveryLocationName",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "LocationUpdatedAtUtc",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "PromptAnswer",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "PromptKey",
                table: "Profiles");
        }
    }
}

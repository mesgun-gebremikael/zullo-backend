using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zullo.Api.Migrations
{
    /// <inheritdoc />
    public partial class BlockedProfileReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SnapshotAge",
                table: "Blocks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotDisplayName",
                table: "Blocks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SnapshotPhotoUrl",
                table: "Blocks",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SnapshotAge",
                table: "Blocks");

            migrationBuilder.DropColumn(
                name: "SnapshotDisplayName",
                table: "Blocks");

            migrationBuilder.DropColumn(
                name: "SnapshotPhotoUrl",
                table: "Blocks");
        }
    }
}

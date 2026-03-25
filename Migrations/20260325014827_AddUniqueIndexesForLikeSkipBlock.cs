using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zullo.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueIndexesForLikeSkipBlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Skips_FromUserId_ToUserId",
                table: "Skips",
                columns: new[] { "FromUserId", "ToUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Likes_FromUserId_ToUserId",
                table: "Likes",
                columns: new[] { "FromUserId", "ToUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Blocks_FromUserId_BlockedUserId",
                table: "Blocks",
                columns: new[] { "FromUserId", "BlockedUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Skips_FromUserId_ToUserId",
                table: "Skips");

            migrationBuilder.DropIndex(
                name: "IX_Likes_FromUserId_ToUserId",
                table: "Likes");

            migrationBuilder.DropIndex(
                name: "IX_Blocks_FromUserId_BlockedUserId",
                table: "Blocks");
        }
    }
}

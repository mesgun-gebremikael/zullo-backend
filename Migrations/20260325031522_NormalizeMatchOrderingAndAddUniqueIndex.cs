using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zullo.Api.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeMatchOrderingAndAddUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Matches_UserAId_UserBId",
                table: "Matches",
                columns: new[] { "UserAId", "UserBId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Matches_UserBId",
                table: "Matches",
                column: "UserBId");

            migrationBuilder.AddForeignKey(
                name: "FK_Matches_User_UserAId",
                table: "Matches",
                column: "UserAId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Matches_User_UserBId",
                table: "Matches",
                column: "UserBId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Matches_User_UserAId",
                table: "Matches");

            migrationBuilder.DropForeignKey(
                name: "FK_Matches_User_UserBId",
                table: "Matches");

            migrationBuilder.DropIndex(
                name: "IX_Matches_UserAId_UserBId",
                table: "Matches");

            migrationBuilder.DropIndex(
                name: "IX_Matches_UserBId",
                table: "Matches");
        }
    }
}

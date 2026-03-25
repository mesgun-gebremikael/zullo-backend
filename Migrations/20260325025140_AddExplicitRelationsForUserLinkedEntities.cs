using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zullo.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExplicitRelationsForUserLinkedEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Skips_ToUserId",
                table: "Skips",
                column: "ToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_FromUserId",
                table: "Reports",
                column: "FromUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ReportedUserId",
                table: "Reports",
                column: "ReportedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Likes_ToUserId",
                table: "Likes",
                column: "ToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Blocks_BlockedUserId",
                table: "Blocks",
                column: "BlockedUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Blocks_User_BlockedUserId",
                table: "Blocks",
                column: "BlockedUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Blocks_User_FromUserId",
                table: "Blocks",
                column: "FromUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Likes_User_FromUserId",
                table: "Likes",
                column: "FromUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Likes_User_ToUserId",
                table: "Likes",
                column: "ToUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_User_FromUserId",
                table: "Messages",
                column: "FromUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_User_ToUserId",
                table: "Messages",
                column: "ToUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Reports_User_FromUserId",
                table: "Reports",
                column: "FromUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Reports_User_ReportedUserId",
                table: "Reports",
                column: "ReportedUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Skips_User_FromUserId",
                table: "Skips",
                column: "FromUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Skips_User_ToUserId",
                table: "Skips",
                column: "ToUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Blocks_User_BlockedUserId",
                table: "Blocks");

            migrationBuilder.DropForeignKey(
                name: "FK_Blocks_User_FromUserId",
                table: "Blocks");

            migrationBuilder.DropForeignKey(
                name: "FK_Likes_User_FromUserId",
                table: "Likes");

            migrationBuilder.DropForeignKey(
                name: "FK_Likes_User_ToUserId",
                table: "Likes");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_User_FromUserId",
                table: "Messages");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_User_ToUserId",
                table: "Messages");

            migrationBuilder.DropForeignKey(
                name: "FK_Reports_User_FromUserId",
                table: "Reports");

            migrationBuilder.DropForeignKey(
                name: "FK_Reports_User_ReportedUserId",
                table: "Reports");

            migrationBuilder.DropForeignKey(
                name: "FK_Skips_User_FromUserId",
                table: "Skips");

            migrationBuilder.DropForeignKey(
                name: "FK_Skips_User_ToUserId",
                table: "Skips");

            migrationBuilder.DropIndex(
                name: "IX_Skips_ToUserId",
                table: "Skips");

            migrationBuilder.DropIndex(
                name: "IX_Reports_FromUserId",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Reports_ReportedUserId",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Likes_ToUserId",
                table: "Likes");

            migrationBuilder.DropIndex(
                name: "IX_Blocks_BlockedUserId",
                table: "Blocks");
        }
    }
}

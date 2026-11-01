using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zullo.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPremiumCommerce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSuperLike",
                table: "Likes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CommerceAccounts",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PremiumUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PremiumPeriodStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BoostAllowancePeriodUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextSuperLikeRefreshUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IncludedSuperLikes = table.Column<int>(type: "integer", nullable: false),
                    IncludedBoosts = table.Column<int>(type: "integer", nullable: false),
                    PurchasedSuperLikes = table.Column<int>(type: "integer", nullable: false),
                    PurchasedBoosts = table.Column<int>(type: "integer", nullable: false),
                    BoostUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HideActivity = table.Column<bool>(type: "boolean", nullable: false),
                    Incognito = table.Column<bool>(type: "boolean", nullable: false),
                    LastActivityUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProviderSnapshotMs = table.Column<long>(type: "bigint", nullable: false),
                    LastVerifiedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceAccounts", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_CommerceAccounts_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseGrants",
                columns: table => new
                {
                    TransactionKey = table.Column<string>(type: "character varying(220)", maxLength: 220, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SuperLikes = table.Column<int>(type: "integer", nullable: false),
                    Boosts = table.Column<int>(type: "integer", nullable: false),
                    Refunded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseGrants", x => x.TransactionKey);
                    table.ForeignKey(
                        name: "FK_PurchaseGrants_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseGrants_UserId",
                table: "PurchaseGrants",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommerceAccounts");

            migrationBuilder.DropTable(
                name: "PurchaseGrants");

            migrationBuilder.DropColumn(
                name: "IsSuperLike",
                table: "Likes");
        }
    }
}

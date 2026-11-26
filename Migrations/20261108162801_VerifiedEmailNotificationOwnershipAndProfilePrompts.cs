using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zullo.Api.Migrations
{
    /// <inheritdoc />
    public partial class VerifiedEmailNotificationOwnershipAndProfilePrompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PurchaseGrants_UserId",
                table: "PurchaseGrants");

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerifiedAtUtc",
                table: "User",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderTransactionId",
                table: "PurchaseGrants",
                type: "character varying(180)",
                maxLength: 180,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConversationPrompts",
                table: "Profiles",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'");

            migrationBuilder.CreateTable(
                name: "MatchNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OtherUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchNotifications_Matches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "Matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingEmailRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextSendAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    SendWindowStartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SendCount = table.Column<int>(type: "integer", nullable: false),
                    DeliveryConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingEmailRegistrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PushInstallations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Token = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushInstallations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushInstallations_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseGrants_UserId_ProductId_ProviderTransactionId",
                table: "PurchaseGrants",
                columns: new[] { "UserId", "ProductId", "ProviderTransactionId" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchNotifications_MatchId_RecipientUserId",
                table: "MatchNotifications",
                columns: new[] { "MatchId", "RecipientUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MatchNotifications_SentAtUtc_CancelledAtUtc_NextAttemptAtUtc",
                table: "MatchNotifications",
                columns: new[] { "SentAtUtc", "CancelledAtUtc", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingEmailRegistrations_ChallengeId",
                table: "PendingEmailRegistrations",
                column: "ChallengeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingEmailRegistrations_Email",
                table: "PendingEmailRegistrations",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingEmailRegistrations_ExpiresAtUtc",
                table: "PendingEmailRegistrations",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PushInstallations_TokenHash",
                table: "PushInstallations",
                column: "TokenHash",
                unique: true,
                filter: "\"TokenHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PushInstallations_UserId",
                table: "PushInstallations",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchNotifications");

            migrationBuilder.DropTable(
                name: "PendingEmailRegistrations");

            migrationBuilder.DropTable(
                name: "PushInstallations");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseGrants_UserId_ProductId_ProviderTransactionId",
                table: "PurchaseGrants");

            migrationBuilder.DropColumn(
                name: "EmailVerifiedAtUtc",
                table: "User");

            migrationBuilder.DropColumn(
                name: "ProviderTransactionId",
                table: "PurchaseGrants");

            migrationBuilder.DropColumn(
                name: "ConversationPrompts",
                table: "Profiles");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseGrants_UserId",
                table: "PurchaseGrants",
                column: "UserId");
        }
    }
}

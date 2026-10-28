using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zullo.Api.Migrations
{
    /// <inheritdoc />
    public partial class ReliableMessageDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeliveryKey",
                table: "Messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeliverySequence",
                table: "Messages",
                type: "bigint",
                nullable: false)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            // Preserve historical order without deleting or rewriting any message content/IDs.
            migrationBuilder.Sql("""
                WITH ordered AS (
                    SELECT "Id", row_number() OVER (ORDER BY "CreatedAtUtc", "Id") AS sequence
                    FROM "Messages"
                )
                UPDATE "Messages" AS message SET "DeliverySequence" = ordered.sequence
                FROM ordered WHERE message."Id" = ordered."Id";
                SELECT setval(pg_get_serial_sequence('"Messages"', 'DeliverySequence'),
                    COALESCE((SELECT MAX("DeliverySequence") FROM "Messages"), 0) + 1, false);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_FromUserId_ToUserId_ClientMessageId",
                table: "Messages",
                columns: new[] { "FromUserId", "ToUserId", "ClientMessageId" },
                filter: "\"DeliveryKey\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_DeliverySequence",
                table: "Messages",
                column: "DeliverySequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_FromUserId_ToUserId_DeliveryKey",
                table: "Messages",
                columns: new[] { "FromUserId", "ToUserId", "DeliveryKey" },
                unique: true,
                filter: "\"DeliveryKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_FromUserId_ToUserId_DeliverySequence",
                table: "Messages",
                columns: new[] { "FromUserId", "ToUserId", "DeliverySequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Messages_FromUserId_ToUserId_ClientMessageId", table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_DeliverySequence",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_FromUserId_ToUserId_DeliveryKey",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_FromUserId_ToUserId_DeliverySequence",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "DeliveryKey",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "DeliverySequence",
                table: "Messages");
        }
    }
}

using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zullo.Api.Migrations
{
    /// <inheritdoc />
    public partial class OwnedPhotoStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastCleanupCheckUtc",
                table: "AccountRemovalRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OwnedPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CloudName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PublicId = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    ProviderAssetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Url = table.Column<string>(type: "text", nullable: true),
                    Aliases = table.Column<List<string>>(type: "jsonb", nullable: false),
                    FileHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UploadFinishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UploadLease = table.Column<Guid>(type: "uuid", nullable: true),
                    UploadLeaseUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CleanupLease = table.Column<Guid>(type: "uuid", nullable: true),
                    CleanupLeaseUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UploadAttempts = table.Column<int>(type: "integer", nullable: false),
                    DeleteAttempts = table.Column<int>(type: "integer", nullable: false),
                    LastErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LegacyApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LegacyApprovalDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnedPhotos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnedPhotos_CloudName_PublicId",
                table: "OwnedPhotos",
                columns: new[] { "CloudName", "PublicId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OwnedPhotos_State_NextAttemptUtc",
                table: "OwnedPhotos",
                columns: new[] { "State", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnedPhotos_UserId_CreatedAtUtc",
                table: "OwnedPhotos",
                columns: new[] { "UserId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnedPhotos");

            migrationBuilder.DropColumn(
                name: "LastCleanupCheckUtc",
                table: "AccountRemovalRequests");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zullo.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExtendedProfileFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Alcohol",
                table: "Profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Cannabis",
                table: "Profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ChildrenCount",
                table: "Profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "HeightCm",
                table: "Profiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobTitle",
                table: "Profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LivePlace",
                table: "Profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OriginPlace",
                table: "Profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RelationshipHistory",
                table: "Profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StudyPlace",
                table: "Profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StudySubject",
                table: "Profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WantChildren",
                table: "Profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WorkPlace",
                table: "Profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WorkStatus",
                table: "Profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ZodiacSign",
                table: "Profiles",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Alcohol",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Cannabis",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "ChildrenCount",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "HeightCm",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "JobTitle",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "LivePlace",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "OriginPlace",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "RelationshipHistory",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "StudyPlace",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "StudySubject",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "WantChildren",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "WorkPlace",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "WorkStatus",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "ZodiacSign",
                table: "Profiles");
        }
    }
}

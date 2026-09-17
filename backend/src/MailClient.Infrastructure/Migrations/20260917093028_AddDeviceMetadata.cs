using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "DeviceTokens",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Platform",
                table: "DeviceTokens",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "AppVersion",
                table: "DeviceTokens",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Locale",
                table: "DeviceTokens",
                type: "character varying(35)",
                maxLength: 35,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppVersion",
                table: "DeviceTokens");

            migrationBuilder.DropColumn(
                name: "Locale",
                table: "DeviceTokens");

            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "DeviceTokens",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.AlterColumn<string>(
                name: "Platform",
                table: "DeviceTokens",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);
        }
    }
}

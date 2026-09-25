using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NotificationPending",
                table: "Mails",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "NotificationPrivacy",
                table: "MailAccounts",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "NotificationsEnabled",
                table: "MailAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyInboxOnly",
                table: "MailAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotificationPending",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "NotificationPrivacy",
                table: "MailAccounts");

            migrationBuilder.DropColumn(
                name: "NotificationsEnabled",
                table: "MailAccounts");

            migrationBuilder.DropColumn(
                name: "NotifyInboxOnly",
                table: "MailAccounts");
        }
    }
}

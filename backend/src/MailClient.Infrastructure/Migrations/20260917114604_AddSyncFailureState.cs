using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncFailureState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailures",
                table: "SyncStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastErrorNotifiedAt",
                table: "SyncStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastFailureAt",
                table: "SyncStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastFailureCategory",
                table: "SyncStates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSuccessfulSyncAt",
                table: "SyncStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRetryAt",
                table: "SyncStates",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsecutiveFailures",
                table: "SyncStates");

            migrationBuilder.DropColumn(
                name: "LastErrorNotifiedAt",
                table: "SyncStates");

            migrationBuilder.DropColumn(
                name: "LastFailureAt",
                table: "SyncStates");

            migrationBuilder.DropColumn(
                name: "LastFailureCategory",
                table: "SyncStates");

            migrationBuilder.DropColumn(
                name: "LastSuccessfulSyncAt",
                table: "SyncStates");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                table: "SyncStates");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledSend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduledSends",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToAddressesJson = table.Column<string>(type: "text", nullable: false),
                    CcAddressesJson = table.Column<string>(type: "text", nullable: false),
                    BccAddressesJson = table.Column<string>(type: "text", nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BodyHtml = table.Column<string>(type: "text", nullable: true),
                    BodyText = table.Column<string>(type: "text", nullable: true),
                    SendAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentMailId = table.Column<Guid>(type: "uuid", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Fingerprint = table.Column<string>(type: "text", nullable: false),
                    ReplySourceMailId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledSends", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledSends_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledSendAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduledSendId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    StoragePath = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledSendAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledSendAttachments_ScheduledSends_ScheduledSendId",
                        column: x => x.ScheduledSendId,
                        principalTable: "ScheduledSends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledSendAttachments_ScheduledSendId",
                table: "ScheduledSendAttachments",
                column: "ScheduledSendId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledSends_MailAccountId_IdempotencyKey",
                table: "ScheduledSends",
                columns: new[] { "MailAccountId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledSends_Status_SendAtUtc",
                table: "ScheduledSends",
                columns: new[] { "Status", "SendAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduledSendAttachments");

            migrationBuilder.DropTable(
                name: "ScheduledSends");
        }
    }
}

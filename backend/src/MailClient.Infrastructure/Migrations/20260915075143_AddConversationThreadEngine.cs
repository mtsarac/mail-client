using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationThreadEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MailHeader_Mails_MailId",
                table: "MailHeader");

            migrationBuilder.DropForeignKey(
                name: "FK_MailParticipant_Mails_MailId",
                table: "MailParticipant");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MailParticipant",
                table: "MailParticipant");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MailHeader",
                table: "MailHeader");

            migrationBuilder.RenameTable(
                name: "MailParticipant",
                newName: "Participants");

            migrationBuilder.RenameTable(
                name: "MailHeader",
                newName: "MailHeaders");

            migrationBuilder.RenameIndex(
                name: "IX_MailParticipant_MailId_Type_SortOrder",
                table: "Participants",
                newName: "IX_Participants_MailId_Type_SortOrder");

            migrationBuilder.RenameIndex(
                name: "IX_MailHeader_MailId_Name",
                table: "MailHeaders",
                newName: "IX_MailHeaders_MailId_Name");

            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "Mails",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Participants",
                table: "Participants",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MailHeaders",
                table: "MailHeaders",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    NormalizedSubject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Mails_ConversationId",
                table: "Mails",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_MailAccountId_LastMessageAt",
                table: "Conversations",
                columns: new[] { "MailAccountId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_MailAccountId_NormalizedSubject",
                table: "Conversations",
                columns: new[] { "MailAccountId", "NormalizedSubject" });

            migrationBuilder.AddForeignKey(
                name: "FK_MailHeaders_Mails_MailId",
                table: "MailHeaders",
                column: "MailId",
                principalTable: "Mails",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Mails_Conversations_ConversationId",
                table: "Mails",
                column: "ConversationId",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Participants_Mails_MailId",
                table: "Participants",
                column: "MailId",
                principalTable: "Mails",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MailHeaders_Mails_MailId",
                table: "MailHeaders");

            migrationBuilder.DropForeignKey(
                name: "FK_Mails_Conversations_ConversationId",
                table: "Mails");

            migrationBuilder.DropForeignKey(
                name: "FK_Participants_Mails_MailId",
                table: "Participants");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Mails_ConversationId",
                table: "Mails");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Participants",
                table: "Participants");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MailHeaders",
                table: "MailHeaders");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "Mails");

            migrationBuilder.RenameTable(
                name: "Participants",
                newName: "MailParticipant");

            migrationBuilder.RenameTable(
                name: "MailHeaders",
                newName: "MailHeader");

            migrationBuilder.RenameIndex(
                name: "IX_Participants_MailId_Type_SortOrder",
                table: "MailParticipant",
                newName: "IX_MailParticipant_MailId_Type_SortOrder");

            migrationBuilder.RenameIndex(
                name: "IX_MailHeaders_MailId_Name",
                table: "MailHeader",
                newName: "IX_MailHeader_MailId_Name");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MailParticipant",
                table: "MailParticipant",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MailHeader",
                table: "MailHeader",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_MailHeader_Mails_MailId",
                table: "MailHeader",
                column: "MailId",
                principalTable: "Mails",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MailParticipant_Mails_MailId",
                table: "MailParticipant",
                column: "MailId",
                principalTable: "Mails",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

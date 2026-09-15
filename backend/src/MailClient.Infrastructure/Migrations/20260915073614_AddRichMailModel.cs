using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRichMailModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Answered",
                table: "Mails",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Deleted",
                table: "Mails",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Draft",
                table: "Mails",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Flagged",
                table: "Mails",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "InReplyToMessageId",
                table: "Mails",
                type: "character varying(998)",
                maxLength: 998,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "InternalDate",
                table: "Mails",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "Recent",
                table: "Mails",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "References",
                table: "Mails",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "SentAt",
                table: "Mails",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "ContentDisposition",
                table: "Attachments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "MailHeader",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false),
                    Value = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailHeader", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailHeader_Mails_MailId",
                        column: x => x.MailId,
                        principalTable: "Mails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailParticipant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    NormalizedAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailParticipant", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailParticipant_Mails_MailId",
                        column: x => x.MailId,
                        principalTable: "Mails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Mails_MessageId",
                table: "Mails",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MailHeader_MailId_Name",
                table: "MailHeader",
                columns: new[] { "MailId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_MailParticipant_MailId_Type_SortOrder",
                table: "MailParticipant",
                columns: new[] { "MailId", "Type", "SortOrder" });

            migrationBuilder.Sql("""
                INSERT INTO "MailParticipant" ("Id", "MailId", "Type", "Address", "NormalizedAddress", "DisplayName", "SortOrder")
                SELECT gen_random_uuid(), m."Id", 0, m."FromAddress", lower(m."FromAddress"), m."FromDisplayName", 0
                FROM "Mails" m
                WHERE m."FromAddress" <> '';

                INSERT INTO "MailParticipant" ("Id", "MailId", "Type", "Address", "NormalizedAddress", "DisplayName", "SortOrder")
                SELECT gen_random_uuid(), m."Id", 1, trim(addr), lower(trim(addr)), '', idx
                FROM "Mails" m
                CROSS JOIN LATERAL unnest(string_to_array(m."ToAddress", ',')) WITH ORDINALITY AS t(addr, idx)
                WHERE addr ILIKE '_%'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.DropTable(
                name: "MailHeader");

            migrationBuilder.DropTable(
                name: "MailParticipant");


            migrationBuilder.DropIndex(
                name: "IX_Mails_MessageId",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "Answered",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "Deleted",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "Draft",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "Flagged",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "InReplyToMessageId",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "InternalDate",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "Recent",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "References",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "SentAt",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "ContentDisposition",
                table: "Attachments");
        }
    }
}

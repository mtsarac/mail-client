using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSignaturesAndIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.AddColumn<Guid>(
                name: "IdentityId",
                table: "ScheduledSends",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultForwardSignatureId",
                table: "MailAccounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultNewSignatureId",
                table: "MailAccounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultReplySignatureId",
                table: "MailAccounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MailSignatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BodyText = table.Column<string>(type: "text", nullable: false),
                    BodyHtml = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailSignatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailSignatures_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    ReplyTo = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    SignatureId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailIdentities_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailIdentities_MailSignatures_SignatureId",
                        column: x => x.SignatureId,
                        principalTable: "MailSignatures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledSends_IdentityId",
                table: "ScheduledSends",
                column: "IdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_MailAccounts_DefaultForwardSignatureId",
                table: "MailAccounts",
                column: "DefaultForwardSignatureId");

            migrationBuilder.CreateIndex(
                name: "IX_MailAccounts_DefaultNewSignatureId",
                table: "MailAccounts",
                column: "DefaultNewSignatureId");

            migrationBuilder.CreateIndex(
                name: "IX_MailAccounts_DefaultReplySignatureId",
                table: "MailAccounts",
                column: "DefaultReplySignatureId");

            migrationBuilder.CreateIndex(
                name: "IX_MailIdentities_MailAccountId",
                table: "MailIdentities",
                column: "MailAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_MailIdentities_SignatureId",
                table: "MailIdentities",
                column: "SignatureId");

            migrationBuilder.CreateIndex(
                name: "IX_MailSignatures_MailAccountId",
                table: "MailSignatures",
                column: "MailAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_MailAccounts_MailSignatures_DefaultForwardSignatureId",
                table: "MailAccounts",
                column: "DefaultForwardSignatureId",
                principalTable: "MailSignatures",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MailAccounts_MailSignatures_DefaultNewSignatureId",
                table: "MailAccounts",
                column: "DefaultNewSignatureId",
                principalTable: "MailSignatures",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MailAccounts_MailSignatures_DefaultReplySignatureId",
                table: "MailAccounts",
                column: "DefaultReplySignatureId",
                principalTable: "MailSignatures",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
            migrationBuilder.Sql("""
                INSERT INTO "MailSignatures" ("Id", "MailAccountId", "Name", "BodyText", "BodyHtml", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), a."Id", 'İmza', a."Signature", NULL, NOW(), NOW()
                FROM "MailAccounts" a
                WHERE NULLIF(TRIM(a."Signature"), '') IS NOT NULL;
                """);
            migrationBuilder.Sql("""
                UPDATE "MailAccounts" a
                SET "DefaultNewSignatureId" = s."Id",
                    "DefaultReplySignatureId" = s."Id",
                    "DefaultForwardSignatureId" = s."Id"
                FROM "MailSignatures" s
                WHERE s."MailAccountId" = a."Id"
                  AND s."Name" = 'İmza'
                  AND NULLIF(TRIM(a."Signature"), '') IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "Signature",
                table: "MailAccounts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MailAccounts_MailSignatures_DefaultForwardSignatureId",
                table: "MailAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_MailAccounts_MailSignatures_DefaultNewSignatureId",
                table: "MailAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_MailAccounts_MailSignatures_DefaultReplySignatureId",
                table: "MailAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_ScheduledSends_MailIdentities_IdentityId",
                table: "ScheduledSends");

            migrationBuilder.DropTable(
                name: "MailIdentities");

            migrationBuilder.DropTable(
                name: "MailSignatures");

            migrationBuilder.DropIndex(
                name: "IX_ScheduledSends_IdentityId",
                table: "ScheduledSends");

            migrationBuilder.DropIndex(
                name: "IX_MailAccounts_DefaultForwardSignatureId",
                table: "MailAccounts");

            migrationBuilder.DropIndex(
                name: "IX_MailAccounts_DefaultNewSignatureId",
                table: "MailAccounts");

            migrationBuilder.DropIndex(
                name: "IX_MailAccounts_DefaultReplySignatureId",
                table: "MailAccounts");

            migrationBuilder.DropColumn(
                name: "IdentityId",
                table: "ScheduledSends");

            migrationBuilder.DropColumn(
                name: "DefaultForwardSignatureId",
                table: "MailAccounts");

            migrationBuilder.DropColumn(
                name: "DefaultNewSignatureId",
                table: "MailAccounts");

            migrationBuilder.DropColumn(
                name: "DefaultReplySignatureId",
                table: "MailAccounts");

            migrationBuilder.AddColumn<string>(
                name: "Signature",
                table: "MailAccounts",
                type: "text",
                nullable: true);
        }
    }
}

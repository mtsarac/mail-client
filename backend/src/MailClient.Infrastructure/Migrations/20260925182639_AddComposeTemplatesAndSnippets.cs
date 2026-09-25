using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddComposeTemplatesAndSnippets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "ScheduledSends",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MailSnippets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailSnippets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailSnippets_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BodyText = table.Column<string>(type: "text", nullable: true),
                    BodyHtml = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailTemplates_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailSnippets_MailAccountId_SortOrder",
                table: "MailSnippets",
                columns: new[] { "MailAccountId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_MailTemplates_MailAccountId_NormalizedName",
                table: "MailTemplates",
                columns: new[] { "MailAccountId", "NormalizedName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailSnippets");

            migrationBuilder.DropTable(
                name: "MailTemplates");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "ScheduledSends");
        }
    }
}

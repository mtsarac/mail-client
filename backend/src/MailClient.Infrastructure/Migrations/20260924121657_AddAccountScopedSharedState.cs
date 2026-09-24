using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountScopedSharedState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Signature",
                table: "MailAccounts",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contacts_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailLabels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Color = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailLabels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailLabels_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailSnoozes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailId = table.Column<Guid>(type: "uuid", nullable: false),
                    UntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailSnoozes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailSnoozes_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailSnoozes_Mails_MailId",
                        column: x => x.MailId,
                        principalTable: "Mails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PinnedMails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailId = table.Column<Guid>(type: "uuid", nullable: false),
                    PinnedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PinnedMails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PinnedMails_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PinnedMails_Mails_MailId",
                        column: x => x.MailId,
                        principalTable: "Mails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailLabelAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailLabelId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailLabelAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailLabelAssignments_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailLabelAssignments_MailLabels_MailLabelId",
                        column: x => x.MailLabelId,
                        principalTable: "MailLabels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailLabelAssignments_Mails_MailId",
                        column: x => x.MailId,
                        principalTable: "Mails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_MailAccountId_NormalizedEmail",
                table: "Contacts",
                columns: new[] { "MailAccountId", "NormalizedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailLabelAssignments_MailAccountId_MailId_MailLabelId",
                table: "MailLabelAssignments",
                columns: new[] { "MailAccountId", "MailId", "MailLabelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailLabelAssignments_MailId",
                table: "MailLabelAssignments",
                column: "MailId");

            migrationBuilder.CreateIndex(
                name: "IX_MailLabelAssignments_MailLabelId",
                table: "MailLabelAssignments",
                column: "MailLabelId");

            migrationBuilder.CreateIndex(
                name: "IX_MailLabels_MailAccountId_Name",
                table: "MailLabels",
                columns: new[] { "MailAccountId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailSnoozes_MailAccountId_MailId",
                table: "MailSnoozes",
                columns: new[] { "MailAccountId", "MailId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailSnoozes_MailId",
                table: "MailSnoozes",
                column: "MailId");

            migrationBuilder.CreateIndex(
                name: "IX_PinnedMails_MailAccountId_MailId",
                table: "PinnedMails",
                columns: new[] { "MailAccountId", "MailId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PinnedMails_MailId",
                table: "PinnedMails",
                column: "MailId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Contacts");

            migrationBuilder.DropTable(
                name: "MailLabelAssignments");

            migrationBuilder.DropTable(
                name: "MailSnoozes");

            migrationBuilder.DropTable(
                name: "PinnedMails");

            migrationBuilder.DropTable(
                name: "MailLabels");

            migrationBuilder.DropColumn(
                name: "Signature",
                table: "MailAccounts");
        }
    }
}

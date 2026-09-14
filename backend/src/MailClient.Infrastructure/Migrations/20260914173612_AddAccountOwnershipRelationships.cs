using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountOwnershipRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Account-owned rows written before these constraints existed can outlive a deleted
            // mailbox; prune them so the new foreign keys apply to existing deployments.
            migrationBuilder.Sql("DELETE FROM \"Attachments\" WHERE \"MailAccountId\" NOT IN (SELECT \"Id\" FROM \"MailAccounts\") OR \"MailId\" NOT IN (SELECT \"Id\" FROM \"Mails\");");
            migrationBuilder.Sql("DELETE FROM \"SyncStates\" WHERE \"MailFolderId\" NOT IN (SELECT \"Id\" FROM \"MailFolders\");");
            migrationBuilder.Sql("DELETE FROM \"SyncSkippedUids\" WHERE \"MailFolderId\" NOT IN (SELECT \"Id\" FROM \"MailFolders\");");
            migrationBuilder.Sql("DELETE FROM \"SendOperations\" WHERE \"MailAccountId\" NOT IN (SELECT \"Id\" FROM \"MailAccounts\");");
            migrationBuilder.Sql("DELETE FROM \"DeviceTokens\" WHERE \"MailAccountId\" NOT IN (SELECT \"Id\" FROM \"MailAccounts\");");
            migrationBuilder.Sql("UPDATE \"AuditLogs\" SET \"MailAccountId\" = NULL WHERE \"MailAccountId\" IS NOT NULL AND \"MailAccountId\" NOT IN (SELECT \"Id\" FROM \"MailAccounts\");");

            migrationBuilder.CreateIndex(
                name: "IX_SyncStates_MailAccountId",
                table: "SyncStates",
                column: "MailAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncSkippedUids_MailAccountId",
                table: "SyncSkippedUids",
                column: "MailAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_Attachments_MailAccounts_MailAccountId",
                table: "Attachments",
                column: "MailAccountId",
                principalTable: "MailAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogs_MailAccounts_MailAccountId",
                table: "AuditLogs",
                column: "MailAccountId",
                principalTable: "MailAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_DeviceTokens_MailAccounts_MailAccountId",
                table: "DeviceTokens",
                column: "MailAccountId",
                principalTable: "MailAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SendOperations_MailAccounts_MailAccountId",
                table: "SendOperations",
                column: "MailAccountId",
                principalTable: "MailAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncSkippedUids_MailAccounts_MailAccountId",
                table: "SyncSkippedUids",
                column: "MailAccountId",
                principalTable: "MailAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncSkippedUids_MailFolders_MailFolderId",
                table: "SyncSkippedUids",
                column: "MailFolderId",
                principalTable: "MailFolders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncStates_MailAccounts_MailAccountId",
                table: "SyncStates",
                column: "MailAccountId",
                principalTable: "MailAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Attachments_MailAccounts_MailAccountId",
                table: "Attachments");

            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogs_MailAccounts_MailAccountId",
                table: "AuditLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_DeviceTokens_MailAccounts_MailAccountId",
                table: "DeviceTokens");

            migrationBuilder.DropForeignKey(
                name: "FK_SendOperations_MailAccounts_MailAccountId",
                table: "SendOperations");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncSkippedUids_MailAccounts_MailAccountId",
                table: "SyncSkippedUids");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncSkippedUids_MailFolders_MailFolderId",
                table: "SyncSkippedUids");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncStates_MailAccounts_MailAccountId",
                table: "SyncStates");

            migrationBuilder.DropIndex(
                name: "IX_SyncStates_MailAccountId",
                table: "SyncStates");

            migrationBuilder.DropIndex(
                name: "IX_SyncSkippedUids_MailAccountId",
                table: "SyncSkippedUids");
        }
    }
}

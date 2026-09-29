using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFolderRoleOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DetectedFolderType",
                table: "MailFolders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE \"MailFolders\" SET \"DetectedFolderType\" = \"FolderType\";");

            migrationBuilder.AddColumn<int>(
                name: "FolderRoleOverride",
                table: "MailFolders",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailFolders_MailAccountId_FolderRoleOverride",
                table: "MailFolders",
                columns: new[] { "MailAccountId", "FolderRoleOverride" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MailFolders_MailAccountId_FolderRoleOverride",
                table: "MailFolders");

            migrationBuilder.DropColumn(
                name: "DetectedFolderType",
                table: "MailFolders");

            migrationBuilder.DropColumn(
                name: "FolderRoleOverride",
                table: "MailFolders");
        }
    }
}

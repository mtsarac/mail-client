using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFolderDelimiter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Delimiter",
                table: "MailFolders",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Delimiter",
                table: "MailFolders");
        }
    }
}

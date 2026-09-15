using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMailOperationReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(name: "PreviousMailFolderId", table: "Mails", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<bool>(name: "NeedsReconciliation", table: "Mails", type: "boolean", nullable: false, defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "PreviousMailFolderId", table: "Mails");
            migrationBuilder.DropColumn(name: "NeedsReconciliation", table: "Mails");
        }
    }
}

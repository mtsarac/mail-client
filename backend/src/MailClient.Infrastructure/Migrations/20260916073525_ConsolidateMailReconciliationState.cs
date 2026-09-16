using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidateMailReconciliationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRestoreReconciliation",
                table: "Mails",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE \"Mails\" SET \"ReconciliationState\" = 1 WHERE \"NeedsReconciliation\" = TRUE AND \"ReconciliationState\" = 0;");
            migrationBuilder.DropColumn(name: "NeedsReconciliation", table: "Mails");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NeedsReconciliation",
                table: "Mails",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE \"Mails\" SET \"NeedsReconciliation\" = TRUE WHERE \"ReconciliationState\" = 1;");
            migrationBuilder.DropColumn(name: "IsRestoreReconciliation", table: "Mails");
        }
    }
}

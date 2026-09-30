using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMailSubstringIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql("""
                CREATE INDEX "IX_Mails_Subject_Substring" ON "Mails" USING gin (lower("Subject") gin_trgm_ops);
                CREATE INDEX "IX_Mails_BodyText_Substring" ON "Mails" USING gin (lower("BodyText") gin_trgm_ops);
                CREATE INDEX "IX_Mails_FromAddress_Substring" ON "Mails" USING gin (lower("FromAddress") gin_trgm_ops);
                CREATE INDEX "IX_Mails_FromDisplayName_Substring" ON "Mails" USING gin (lower("FromDisplayName") gin_trgm_ops);
                CREATE INDEX "IX_Mails_ToAddress_Substring" ON "Mails" USING gin (lower("ToAddress") gin_trgm_ops);
                CREATE INDEX "IX_Mails_MessageId_Substring" ON "Mails" USING gin (lower("MessageId") gin_trgm_ops);
                CREATE INDEX "IX_Participants_Address_Substring" ON "Participants" USING gin (lower("Address") gin_trgm_ops);
                CREATE INDEX "IX_Participants_DisplayName_Substring" ON "Participants" USING gin (lower("DisplayName") gin_trgm_ops);
                CREATE INDEX "IX_Attachments_FileName_Substring" ON "Attachments" USING gin (lower("FileName") gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX "IX_Mails_Subject_Substring";
                DROP INDEX "IX_Mails_BodyText_Substring";
                DROP INDEX "IX_Mails_FromAddress_Substring";
                DROP INDEX "IX_Mails_FromDisplayName_Substring";
                DROP INDEX "IX_Mails_ToAddress_Substring";
                DROP INDEX "IX_Mails_MessageId_Substring";
                DROP INDEX "IX_Participants_Address_Substring";
                DROP INDEX "IX_Participants_DisplayName_Substring";
                DROP INDEX "IX_Attachments_FileName_Substring";
                """);
        }
    }
}

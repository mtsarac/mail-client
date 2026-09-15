using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillSentAtInternalDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Mails"
                SET "SentAt" = "ReceivedAt",
                    "InternalDate" = "ReceivedAt"
                WHERE "SentAt" = TIMESTAMPTZ '0001-01-01 00:00:00+00'
                   OR "InternalDate" = TIMESTAMPTZ '0001-01-01 00:00:00+00';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}

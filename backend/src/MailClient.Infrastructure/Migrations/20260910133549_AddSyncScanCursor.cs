using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncScanCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "NextUidScanStart",
                table: "SyncStates",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            // Backfill from already checkpointed progress: migrated rows must
            // continue scanning right after their LastUid instead of restarting
            // at UID 1. A fully scanned 32-bit UID space keeps the one-past-end
            // sentinel (uint.MaxValue + 1), which bigint can represent.
            migrationBuilder.Sql("""
                UPDATE "SyncStates"
                SET "NextUidScanStart" =
                    CASE
                        WHEN "LastUid" >= 4294967295 THEN 4294967296
                        ELSE "LastUid" + 1
                    END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NextUidScanStart",
                table: "SyncStates");
        }
    }
}

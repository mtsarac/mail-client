using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: false),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<string>(type: "text", nullable: true),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "text", nullable: true),
                    Metadata = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Token = table.Column<string>(type: "text", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    RegisteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MailAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    NormalizedEmailAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    AuthenticationMethod = table.Column<int>(type: "integer", nullable: false),
                    ImapHost = table.Column<string>(type: "text", nullable: false),
                    ImapPort = table.Column<int>(type: "integer", nullable: false),
                    ImapSecurity = table.Column<int>(type: "integer", nullable: false),
                    SmtpHost = table.Column<string>(type: "text", nullable: false),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpSecurity = table.Column<int>(type: "integer", nullable: false),
                    DiscoverySource = table.Column<int>(type: "integer", nullable: false),
                    SaveSentCopy = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAuthenticatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SendOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SentCopySaved = table.Column<bool>(type: "boolean", nullable: false),
                    Warning = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SendOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncSkippedUids",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailFolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Uid = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    SkippedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncSkippedUids", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MailCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthenticationMethod = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    EncryptedMaterial = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Scopes = table.Column<string>(type: "text", nullable: true),
                    ProviderMetadata = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailCredentials_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    FullName = table.Column<string>(type: "text", nullable: false),
                    FolderType = table.Column<int>(type: "integer", nullable: false),
                    UidValidity = table.Column<long>(type: "bigint", nullable: false),
                    IsSyncEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailFolders_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RefreshTokenHash = table.Column<string>(type: "text", nullable: false),
                    DeviceIdentifier = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplacedBySessionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailSessions_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Mails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailFolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Uid = table.Column<long>(type: "bigint", nullable: false),
                    UidValidity = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    FromDisplayName = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    ToAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    BodyText = table.Column<string>(type: "text", nullable: false),
                    BodyHtml = table.Column<string>(type: "text", nullable: false),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    HasAttachments = table.Column<bool>(type: "boolean", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Mails_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Mails_MailFolders_MailFolderId",
                        column: x => x.MailFolderId,
                        principalTable: "MailFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailFolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    UidValidity = table.Column<long>(type: "bigint", nullable: false),
                    LastUid = table.Column<long>(type: "bigint", nullable: false),
                    NextUidScanStart = table.Column<long>(type: "bigint", nullable: false),
                    LastNewMailSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastFlagSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncStates_MailFolders_MailFolderId",
                        column: x => x.MailFolderId,
                        principalTable: "MailFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    StoragePath = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    IsInline = table.Column<bool>(type: "boolean", nullable: false),
                    ContentId = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attachments_Mails_MailId",
                        column: x => x.MailId,
                        principalTable: "Mails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_MailAccountId_MailId",
                table: "Attachments",
                columns: new[] { "MailAccountId", "MailId" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_MailId",
                table: "Attachments",
                column: "MailId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_MailAccountId_TimestampUtc",
                table: "AuditLogs",
                columns: new[] { "MailAccountId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_MailAccountId_Token",
                table: "DeviceTokens",
                columns: new[] { "MailAccountId", "Token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailAccounts_NormalizedEmailAddress",
                table: "MailAccounts",
                column: "NormalizedEmailAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailCredentials_MailAccountId_AuthenticationMethod",
                table: "MailCredentials",
                columns: new[] { "MailAccountId", "AuthenticationMethod" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailFolders_MailAccountId_FullName",
                table: "MailFolders",
                columns: new[] { "MailAccountId", "FullName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Mails_MailAccountId",
                table: "Mails",
                column: "MailAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Mails_MailFolderId_Uid",
                table: "Mails",
                columns: new[] { "MailFolderId", "Uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailSessions_MailAccountId",
                table: "MailSessions",
                column: "MailAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_MailSessions_RefreshTokenHash",
                table: "MailSessions",
                column: "RefreshTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SendOperations_MailAccountId_IdempotencyKey",
                table: "SendOperations",
                columns: new[] { "MailAccountId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncSkippedUids_MailFolderId_Uid",
                table: "SyncSkippedUids",
                columns: new[] { "MailFolderId", "Uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncStates_MailFolderId",
                table: "SyncStates",
                column: "MailFolderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "DeviceTokens");

            migrationBuilder.DropTable(
                name: "MailCredentials");

            migrationBuilder.DropTable(
                name: "MailSessions");

            migrationBuilder.DropTable(
                name: "SendOperations");

            migrationBuilder.DropTable(
                name: "SyncSkippedUids");

            migrationBuilder.DropTable(
                name: "SyncStates");

            migrationBuilder.DropTable(
                name: "Mails");

            migrationBuilder.DropTable(
                name: "MailFolders");

            migrationBuilder.DropTable(
                name: "MailAccounts");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MergeSameMessageIdConversations : Migration
    {
        /// <summary>
        /// Copies of one message (same Message-ID in one account) used to open separate conversations. Folds each such
        /// conversation into the oldest one sharing a Message-ID — same survivor rule as ConversationService — until
        /// nothing is left to merge, so chains of split copies collapse transitively.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE merged integer;
                BEGIN
                    LOOP
                        CREATE TEMP TABLE conversation_merge ON COMMIT DROP AS
                        SELECT DISTINCT ON (m."ConversationId") m."ConversationId" AS loser, s."Id" AS survivor
                        FROM "Mails" m
                        JOIN LATERAL (
                            SELECT c."Id", c."StartedAt"
                            FROM "Mails" o
                            JOIN "Conversations" c ON c."Id" = o."ConversationId"
                            WHERE o."MailAccountId" = m."MailAccountId" AND o."MessageId" = m."MessageId"
                            ORDER BY c."StartedAt", c."Id"
                            LIMIT 1) s ON TRUE
                        WHERE m."MessageId" <> '' AND m."ConversationId" IS NOT NULL AND m."ConversationId" <> s."Id"
                        ORDER BY m."ConversationId", s."StartedAt", s."Id";

                        GET DIAGNOSTICS merged = ROW_COUNT;
                        IF merged = 0 THEN
                            DROP TABLE conversation_merge;
                            EXIT;
                        END IF;

                        -- A survivor that is itself folded away this pass would orphan the mails moved onto it;
                        -- leave that link for the next pass. Survivors always sort strictly older, so progress is kept.
                        DELETE FROM conversation_merge WHERE survivor IN (SELECT loser FROM conversation_merge);

                        UPDATE "Conversations" c
                        SET "StartedAt" = LEAST(c."StartedAt", b.started),
                            "LastMessageAt" = GREATEST(c."LastMessageAt", b.last)
                        FROM (
                            SELECT x.survivor, MIN(l."StartedAt") AS started, MAX(l."LastMessageAt") AS last
                            FROM conversation_merge x
                            JOIN "Conversations" l ON l."Id" = x.loser
                            GROUP BY x.survivor) b
                        WHERE c."Id" = b.survivor;

                        UPDATE "Mails" m SET "ConversationId" = x.survivor
                        FROM conversation_merge x WHERE m."ConversationId" = x.loser;


                        DELETE FROM "Conversations" c USING conversation_merge x WHERE c."Id" = x.loser;
                        DROP TABLE conversation_merge;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}

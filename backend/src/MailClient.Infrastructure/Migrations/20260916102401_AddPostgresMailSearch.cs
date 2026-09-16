using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace MailClient.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPostgresMailSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "Mails",
                type: "tsvector",
                nullable: true)
                .Annotation("Npgsql:TsVectorConfig", "simple")
                .Annotation("Npgsql:TsVectorProperties", new[] { "Subject", "BodyText", "FromAddress", "FromDisplayName", "ToAddress", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_Mails_SearchVector",
                table: "Mails",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Mails_SearchVector",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "SearchVector",
                table: "Mails");
        }
    }
}

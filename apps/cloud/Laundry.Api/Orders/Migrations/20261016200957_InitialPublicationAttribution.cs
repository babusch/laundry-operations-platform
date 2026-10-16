using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Api.Orders.Migrations
{
    /// <inheritdoc />
    public partial class InitialPublicationAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "initial_publication_actor",
                schema: "orders",
                table: "publication_outbox",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "initial_publication_actor",
                schema: "orders",
                table: "publication_outbox");
        }
    }
}

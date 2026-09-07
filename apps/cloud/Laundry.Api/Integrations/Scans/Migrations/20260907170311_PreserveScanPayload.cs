using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Api.Integrations.Scans.Migrations
{
    /// <inheritdoc />
    public partial class PreserveScanPayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "payload_json",
                schema: "integrations",
                table: "scan_observations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "payload_json",
                schema: "integrations",
                table: "scan_observations");
        }
    }
}

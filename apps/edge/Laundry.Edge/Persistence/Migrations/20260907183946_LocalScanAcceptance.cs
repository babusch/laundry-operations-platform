using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Edge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LocalScanAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "plant");

            migrationBuilder.CreateTable(
                name: "observations",
                schema: "plant",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accepted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submission_json = table.Column<string>(type: "text", nullable: false),
                    event_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_observations", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "plant",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.event_id);
                    table.ForeignKey(
                        name: "FK_outbox_observations_event_id",
                        column: x => x.event_id,
                        principalSchema: "plant",
                        principalTable: "observations",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_observations_tenant_id_plant_id_accepted_at_utc",
                schema: "plant",
                table: "observations",
                columns: new[] { "tenant_id", "plant_id", "accepted_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_status",
                schema: "plant",
                table: "outbox",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox",
                schema: "plant");

            migrationBuilder.DropTable(
                name: "observations",
                schema: "plant");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Edge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplayAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "replay_audit",
                schema: "plant",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_attempts = table.Column<int>(type: "integer", nullable: false),
                    previous_error = table.Column<string>(type: "text", nullable: true),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_replay_audit", x => x.request_id);
                    table.ForeignKey(
                        name: "FK_replay_audit_observations_event_id",
                        column: x => x.event_id,
                        principalSchema: "plant",
                        principalTable: "observations",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_replay_audit_event_id",
                schema: "plant",
                table: "replay_audit",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "IX_replay_audit_tenant_id_plant_id_event_id",
                schema: "plant",
                table: "replay_audit",
                columns: new[] { "tenant_id", "plant_id", "event_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "replay_audit",
                schema: "plant");
        }
    }
}

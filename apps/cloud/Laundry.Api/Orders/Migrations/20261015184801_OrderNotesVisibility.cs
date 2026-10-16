using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Api.Orders.Migrations
{
    /// <inheritdoc />
    public partial class OrderNotesVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "notes_visible_to_operators",
                schema: "orders",
                table: "service_orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "note_defaults",
                schema: "orders",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notes_visible_to_operators = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_defaults", x => new { x.tenant_id, x.plant_id });
                    table.CheckConstraint("ck_note_default_version", "version > 0");
                });

            migrationBuilder.CreateTable(
                name: "note_default_commands",
                schema: "orders",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_issuer = table.Column<string>(type: "text", nullable: false),
                    actor_subject = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_version = table.Column<long>(type: "bigint", nullable: false),
                    before_visible = table.Column<bool>(type: "boolean", nullable: false),
                    after_visible = table.Column<bool>(type: "boolean", nullable: false),
                    result_version = table.Column<long>(type: "bigint", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_default_commands", x => new { x.tenant_id, x.plant_id, x.actor_issuer, x.actor_subject, x.idempotency_key });
                    table.CheckConstraint("ck_note_default_command_versions", "expected_version >= 0 AND result_version > 0");
                    table.ForeignKey(
                        name: "FK_note_default_commands_note_defaults_tenant_id_plant_id",
                        columns: x => new { x.tenant_id, x.plant_id },
                        principalSchema: "orders",
                        principalTable: "note_defaults",
                        principalColumns: new[] { "tenant_id", "plant_id" },
                        onDelete: ReferentialAction.Restrict);
                });
            migrationBuilder.Sql("""
                CREATE TRIGGER immutable_note_default_command
                    BEFORE UPDATE OR DELETE ON orders.note_default_commands
                    FOR EACH ROW EXECUTE FUNCTION orders.reject_history_mutation();
                CREATE TRIGGER no_truncate_note_default_command
                    BEFORE TRUNCATE ON orders.note_default_commands
                    FOR EACH STATEMENT EXECUTE FUNCTION orders.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "note_default_commands",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "note_defaults",
                schema: "orders");

            migrationBuilder.DropColumn(
                name: "notes_visible_to_operators",
                schema: "orders",
                table: "service_orders");
        }
    }
}

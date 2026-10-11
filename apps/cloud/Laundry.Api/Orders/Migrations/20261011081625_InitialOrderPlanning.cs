using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Api.Orders.Migrations
{
    /// <inheritdoc />
    public partial class InitialOrderPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orders");

            migrationBuilder.CreateTable(
                name: "number_counters",
                schema: "orders",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_number = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_number_counters", x => new { x.tenant_id, x.plant_id });
                });

            migrationBuilder.CreateTable(
                name: "service_orders",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "text", nullable: false),
                    planned_receiving_date = table.Column<DateOnly>(type: "date", nullable: false),
                    planned_delivery_date = table.Column<DateOnly>(type: "date", nullable: false),
                    customer_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_issuer = table.Column<string>(type: "text", nullable: false),
                    created_by_subject = table.Column<string>(type: "text", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_issuer = table.Column<string>(type: "text", nullable: false),
                    updated_by_subject = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_orders", x => x.id);
                    table.UniqueConstraint("AK_service_orders_tenant_id_plant_id_id", x => new { x.tenant_id, x.plant_id, x.id });
                    table.CheckConstraint("ck_order_dates", "planned_delivery_date >= planned_receiving_date");
                    table.CheckConstraint("ck_order_status", "status = 'planned'");
                    table.CheckConstraint("ck_order_version", "version > 0");
                });

            migrationBuilder.CreateTable(
                name: "command_receipts",
                schema: "orders",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_issuer = table.Column<string>(type: "text", nullable: false),
                    actor_subject = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "text", nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "jsonb", nullable: false),
                    response_etag = table.Column<string>(type: "text", nullable: false),
                    response_location = table.Column<string>(type: "text", nullable: false),
                    accepted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_command_receipts", x => new { x.tenant_id, x.plant_id, x.actor_issuer, x.actor_subject, x.idempotency_key });
                    table.ForeignKey(
                        name: "FK_command_receipts_service_orders_tenant_id_plant_id_order_id",
                        columns: x => new { x.tenant_id, x.plant_id, x.order_id },
                        principalSchema: "orders",
                        principalTable: "service_orders",
                        principalColumns: new[] { "tenant_id", "plant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_audit_entries",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    order_version = table.Column<long>(type: "bigint", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_issuer = table.Column<string>(type: "text", nullable: false),
                    actor_subject = table.Column<string>(type: "text", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    before_plan = table.Column<string>(type: "jsonb", nullable: true),
                    after_plan = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_audit_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_audit_entries_service_orders_tenant_id_plant_id_order~",
                        columns: x => new { x.tenant_id, x.plant_id, x.order_id },
                        principalSchema: "orders",
                        principalTable: "service_orders",
                        principalColumns: new[] { "tenant_id", "plant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_command_receipts_tenant_id_plant_id_order_id",
                schema: "orders",
                table: "command_receipts",
                columns: new[] { "tenant_id", "plant_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "IX_order_audit_entries_tenant_id_plant_id_order_id_order_versi~",
                schema: "orders",
                table: "order_audit_entries",
                columns: new[] { "tenant_id", "plant_id", "order_id", "order_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_orders_tenant_id_plant_id_customer_id_planned_recei~",
                schema: "orders",
                table: "service_orders",
                columns: new[] { "tenant_id", "plant_id", "customer_id", "planned_receiving_date", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_service_orders_tenant_id_plant_id_order_number",
                schema: "orders",
                table: "service_orders",
                columns: new[] { "tenant_id", "plant_id", "order_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_orders_tenant_id_plant_id_planned_receiving_date_id",
                schema: "orders",
                table: "service_orders",
                columns: new[] { "tenant_id", "plant_id", "planned_receiving_date", "id" });

            migrationBuilder.Sql("""
                CREATE FUNCTION orders.reject_history_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Order history and successful receipts are append-only';
                END;
                $$;
                CREATE TRIGGER immutable_order_audit
                    BEFORE UPDATE OR DELETE ON orders.order_audit_entries
                    FOR EACH ROW EXECUTE FUNCTION orders.reject_history_mutation();
                CREATE TRIGGER immutable_order_receipt
                    BEFORE UPDATE OR DELETE ON orders.command_receipts
                    FOR EACH ROW EXECUTE FUNCTION orders.reject_history_mutation();
                CREATE TRIGGER no_truncate_order_audit
                    BEFORE TRUNCATE ON orders.order_audit_entries
                    FOR EACH STATEMENT EXECUTE FUNCTION orders.reject_history_mutation();
                CREATE TRIGGER no_truncate_order_receipt
                    BEFORE TRUNCATE ON orders.command_receipts
                    FOR EACH STATEMENT EXECUTE FUNCTION orders.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "command_receipts",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "number_counters",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "order_audit_entries",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "service_orders",
                schema: "orders");

            migrationBuilder.Sql("DROP FUNCTION orders.reject_history_mutation()");
        }
    }
}

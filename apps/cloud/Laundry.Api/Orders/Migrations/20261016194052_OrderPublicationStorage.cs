using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Api.Orders.Migrations
{
    /// <inheritdoc />
    public partial class OrderPublicationStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "publication_heads",
                schema: "orders",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    content_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publication_heads", x => new { x.tenant_id, x.plant_id, x.order_id });
                    table.CheckConstraint("ck_publication_head_version", "version BETWEEN 1 AND 9007199254740991");
                    table.ForeignKey(
                        name: "FK_publication_heads_service_orders_tenant_id_plant_id_order_id",
                        columns: x => new { x.tenant_id, x.plant_id, x.order_id },
                        principalSchema: "orders",
                        principalTable: "service_orders",
                        principalColumns: new[] { "tenant_id", "plant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "publication_outbox",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publication_outbox", x => x.id);
                    table.CheckConstraint("ck_publication_outbox_version", "version BETWEEN 1 AND 9007199254740991");
                    table.ForeignKey(
                        name: "FK_publication_outbox_service_orders_tenant_id_plant_id_order_~",
                        columns: x => new { x.tenant_id, x.plant_id, x.order_id },
                        principalSchema: "orders",
                        principalTable: "service_orders",
                        principalColumns: new[] { "tenant_id", "plant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_publication_outbox_tenant_id_plant_id_order_id_version",
                schema: "orders",
                table: "publication_outbox",
                columns: new[] { "tenant_id", "plant_id", "order_id", "version" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE TRIGGER immutable_publication_outbox
                    BEFORE UPDATE OR DELETE ON orders.publication_outbox
                    FOR EACH ROW EXECUTE FUNCTION orders.reject_history_mutation();
                CREATE TRIGGER no_truncate_publication_outbox
                    BEFORE TRUNCATE ON orders.publication_outbox
                    FOR EACH STATEMENT EXECUTE FUNCTION orders.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "publication_heads",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "publication_outbox",
                schema: "orders");
        }
    }
}

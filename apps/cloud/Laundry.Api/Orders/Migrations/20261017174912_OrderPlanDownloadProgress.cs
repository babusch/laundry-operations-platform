using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Api.Orders.Migrations
{
    /// <inheritdoc />
    public partial class OrderPlanDownloadProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "change_position",
                schema: "orders",
                table: "publication_heads",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "publication_feed_states",
                schema: "orders",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    epoch = table.Column<Guid>(type: "uuid", nullable: false),
                    last_position = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publication_feed_states", x => new { x.tenant_id, x.plant_id });
                    table.CheckConstraint("ck_publication_feed_epoch", "epoch <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_publication_feed_position", "last_position > 0");
                });

            // Existing current copies become the initial feed, without rewriting immutable
            // outbox payloads or inventing a historical cross-order commit order. Writers
            // must be stopped for this explicitly applied migration.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT tenant_id, plant_id, order_id,
                           row_number() OVER (PARTITION BY tenant_id, plant_id ORDER BY order_id) AS position
                    FROM orders.publication_heads
                )
                UPDATE orders.publication_heads h SET change_position = r.position
                FROM ranked r WHERE h.tenant_id=r.tenant_id AND h.plant_id=r.plant_id AND h.order_id=r.order_id;
                INSERT INTO orders.publication_feed_states (tenant_id, plant_id, epoch, last_position)
                SELECT tenant_id, plant_id, gen_random_uuid(), max(change_position)
                FROM orders.publication_heads GROUP BY tenant_id, plant_id;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_publication_heads_tenant_id_plant_id_change_position",
                schema: "orders",
                table: "publication_heads",
                columns: new[] { "tenant_id", "plant_id", "change_position" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_publication_head_position",
                schema: "orders",
                table: "publication_heads",
                sql: "change_position > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "publication_feed_states",
                schema: "orders");

            migrationBuilder.DropIndex(
                name: "IX_publication_heads_tenant_id_plant_id_change_position",
                schema: "orders",
                table: "publication_heads");

            migrationBuilder.DropCheckConstraint(
                name: "ck_publication_head_position",
                schema: "orders",
                table: "publication_heads");

            migrationBuilder.DropColumn(
                name: "change_position",
                schema: "orders",
                table: "publication_heads");
        }
    }
}

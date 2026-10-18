using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Edge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LocalOrderDownloadStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_download_progress",
                schema: "plant",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    cursor = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    last_successful_check_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_download_progress", x => new { x.tenant_id, x.plant_id });
                    table.CheckConstraint("ck_order_download_progress_revision", "revision > 0");
                });

            migrationBuilder.CreateTable(
                name: "order_plans",
                schema: "plant",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    content_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    stored_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_plans", x => new { x.tenant_id, x.plant_id, x.order_id });
                    table.CheckConstraint("ck_local_order_revision", "version BETWEEN 1 AND 9007199254740991");
                });

            migrationBuilder.CreateTable(
                name: "order_download_receipts",
                schema: "plant",
                columns: table => new
                {
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    progress_revision = table.Column<long>(type: "bigint", nullable: false),
                    applied = table.Column<int>(type: "integer", nullable: false),
                    duplicates = table.Column<int>(type: "integer", nullable: false),
                    older = table.Column<int>(type: "integer", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_download_receipts", x => x.batch_id);
                    table.CheckConstraint("ck_order_download_receipt_counts", "applied >= 0 AND duplicates >= 0 AND older >= 0 AND applied + duplicates + older <= 100");
                    table.CheckConstraint("ck_order_download_receipt_revision", "progress_revision > 0");
                    table.ForeignKey(
                        name: "FK_order_download_receipts_order_download_progress_tenant_id_p~",
                        columns: x => new { x.tenant_id, x.plant_id },
                        principalSchema: "plant",
                        principalTable: "order_download_progress",
                        principalColumns: new[] { "tenant_id", "plant_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_download_receipts_tenant_id_plant_id_progress_revision",
                schema: "plant",
                table: "order_download_receipts",
                columns: new[] { "tenant_id", "plant_id", "progress_revision" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION plant.reject_order_download_receipt_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'order_download_receipt_is_append_only' USING ERRCODE = '23514';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER immutable_order_download_receipt
                    BEFORE UPDATE OR DELETE ON plant.order_download_receipts
                    FOR EACH ROW EXECUTE FUNCTION plant.reject_order_download_receipt_mutation();
                CREATE TRIGGER no_truncate_order_download_receipts
                    BEFORE TRUNCATE ON plant.order_download_receipts
                    FOR EACH STATEMENT EXECUTE FUNCTION plant.reject_order_download_receipt_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_download_receipts",
                schema: "plant");

            migrationBuilder.DropTable(
                name: "order_plans",
                schema: "plant");

            migrationBuilder.DropTable(
                name: "order_download_progress",
                schema: "plant");
            migrationBuilder.Sql("DROP FUNCTION plant.reject_order_download_receipt_mutation();");
        }
    }
}

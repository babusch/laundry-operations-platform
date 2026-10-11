using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Api.Customers.Migrations
{
    /// <inheritdoc />
    public partial class InitialCustomerEligibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "customers");

            migrationBuilder.CreateTable(
                name: "laundry_customers",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_laundry_customers", x => x.id);
                    table.UniqueConstraint("AK_laundry_customers_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_customer_labels", "length(btrim(code)) > 0 AND length(btrim(display_name)) > 0");
                    table.CheckConstraint("ck_customer_scope", "tenant_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_customer_version", "version > 0");
                });

            migrationBuilder.CreateTable(
                name: "plant_services",
                schema: "customers",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plant_services", x => new { x.tenant_id, x.plant_id, x.customer_id });
                    table.CheckConstraint("ck_customer_service_plant", "plant_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_plant_services_laundry_customers_tenant_id_customer_id",
                        columns: x => new { x.tenant_id, x.customer_id },
                        principalSchema: "customers",
                        principalTable: "laundry_customers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "setup_audit_entries",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_by = table.Column<string>(type: "text", nullable: false),
                    after_state = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_audit_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_setup_audit_entries_laundry_customers_tenant_id_customer_id",
                        columns: x => new { x.tenant_id, x.customer_id },
                        principalSchema: "customers",
                        principalTable: "laundry_customers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_laundry_customers_tenant_id_code",
                schema: "customers",
                table: "laundry_customers",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plant_services_tenant_id_customer_id",
                schema: "customers",
                table: "plant_services",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_setup_audit_entries_tenant_id_customer_id",
                schema: "customers",
                table: "setup_audit_entries",
                columns: new[] { "tenant_id", "customer_id" });
            migrationBuilder.Sql("""
                CREATE FUNCTION customers.reject_setup_audit_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Customer setup history is append-only';
                END;
                $$;
                CREATE TRIGGER immutable_customer_setup_audit
                    BEFORE UPDATE OR DELETE ON customers.setup_audit_entries
                    FOR EACH ROW EXECUTE FUNCTION customers.reject_setup_audit_mutation();
                CREATE TRIGGER no_truncate_customer_setup_audit
                    BEFORE TRUNCATE ON customers.setup_audit_entries
                    FOR EACH STATEMENT EXECUTE FUNCTION customers.reject_setup_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plant_services",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "setup_audit_entries",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "laundry_customers",
                schema: "customers");
            migrationBuilder.Sql("DROP FUNCTION customers.reject_setup_audit_mutation()");
        }
    }
}

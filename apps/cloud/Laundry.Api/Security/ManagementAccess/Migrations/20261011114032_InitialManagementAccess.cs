using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Api.Security.ManagementAccess.Migrations
{
    /// <inheritdoc />
    public partial class InitialManagementAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity_access");

            migrationBuilder.CreateTable(
                name: "plant_memberships",
                schema: "identity_access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_issuer = table.Column<string>(type: "text", nullable: false),
                    actor_subject = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plant_memberships", x => x.id);
                    table.CheckConstraint("ck_membership_identity", "length(btrim(actor_issuer)) > 0 AND length(btrim(actor_subject)) > 0");
                    table.CheckConstraint("ck_membership_scope", "tenant_id <> '00000000-0000-0000-0000-000000000000'::uuid AND plant_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_membership_version", "version > 0");
                });

            migrationBuilder.CreateTable(
                name: "membership_audit_entries",
                schema: "identity_access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_version = table.Column<long>(type: "bigint", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_by = table.Column<string>(type: "text", nullable: false),
                    after_state = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_audit_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_membership_audit_entries_plant_memberships_membership_id",
                        column: x => x.membership_id,
                        principalSchema: "identity_access",
                        principalTable: "plant_memberships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plant_membership_permissions",
                schema: "identity_access",
                columns: table => new
                {
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plant_membership_permissions", x => new { x.membership_id, x.permission });
                    table.CheckConstraint("ck_membership_permission", "permission IN ('orders.read', 'orders.create', 'orders.plan.edit')");
                    table.ForeignKey(
                        name: "FK_plant_membership_permissions_plant_memberships_membership_id",
                        column: x => x.membership_id,
                        principalSchema: "identity_access",
                        principalTable: "plant_memberships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_membership_audit_entries_membership_id_membership_version",
                schema: "identity_access",
                table: "membership_audit_entries",
                columns: new[] { "membership_id", "membership_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plant_memberships_actor_issuer_actor_subject_tenant_id_plan~",
                schema: "identity_access",
                table: "plant_memberships",
                columns: new[] { "actor_issuer", "actor_subject", "tenant_id", "plant_id" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION identity_access.reject_audit_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Membership history is append-only';
                END;
                $$;
                CREATE TRIGGER immutable_membership_audit
                    BEFORE UPDATE OR DELETE ON identity_access.membership_audit_entries
                    FOR EACH ROW EXECUTE FUNCTION identity_access.reject_audit_mutation();
                CREATE TRIGGER no_truncate_membership_audit
                    BEFORE TRUNCATE ON identity_access.membership_audit_entries
                    FOR EACH STATEMENT EXECUTE FUNCTION identity_access.reject_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "membership_audit_entries",
                schema: "identity_access");

            migrationBuilder.DropTable(
                name: "plant_membership_permissions",
                schema: "identity_access");

            migrationBuilder.DropTable(
                name: "plant_memberships",
                schema: "identity_access");
            migrationBuilder.Sql("DROP FUNCTION identity_access.reject_audit_mutation()");
        }
    }
}

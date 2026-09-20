using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Edge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrustedSourceSecurityFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trusted_sources",
                schema: "plant",
                columns: table => new
                {
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    station_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    configuration_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trusted_sources", x => x.source_id);
                });

            migrationBuilder.CreateTable(
                name: "source_credentials",
                schema: "plant",
                columns: table => new
                {
                    credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    verifier_digest = table.Column<byte[]>(type: "bytea", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_credentials", x => x.credential_id);
                    table.ForeignKey(
                        name: "FK_source_credentials_trusted_sources_source_id",
                        column: x => x.source_id,
                        principalSchema: "plant",
                        principalTable: "trusted_sources",
                        principalColumn: "source_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "source_enrollment_codes",
                schema: "plant",
                columns: table => new
                {
                    enrollment_code_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_digest = table.Column<byte[]>(type: "bytea", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    redeemed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_enrollment_codes", x => x.enrollment_code_id);
                    table.ForeignKey(
                        name: "FK_source_enrollment_codes_trusted_sources_source_id",
                        column: x => x.source_id,
                        principalSchema: "plant",
                        principalTable: "trusted_sources",
                        principalColumn: "source_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "source_permissions",
                schema: "plant",
                columns: table => new
                {
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_permissions", x => new { x.source_id, x.permission });
                    table.ForeignKey(
                        name: "FK_source_permissions_trusted_sources_source_id",
                        column: x => x.source_id,
                        principalSchema: "plant",
                        principalTable: "trusted_sources",
                        principalColumn: "source_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "source_security_audit",
                schema: "plant",
                columns: table => new
                {
                    audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_type = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_security_audit", x => x.audit_id);
                    table.ForeignKey(
                        name: "FK_source_security_audit_source_credentials_credential_id",
                        column: x => x.credential_id,
                        principalSchema: "plant",
                        principalTable: "source_credentials",
                        principalColumn: "credential_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_source_security_audit_trusted_sources_source_id",
                        column: x => x.source_id,
                        principalSchema: "plant",
                        principalTable: "trusted_sources",
                        principalColumn: "source_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_source_credentials_credential_kind_verifier_digest",
                schema: "plant",
                table: "source_credentials",
                columns: new[] { "credential_kind", "verifier_digest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_credentials_source_id",
                schema: "plant",
                table: "source_credentials",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "IX_source_enrollment_codes_code_digest",
                schema: "plant",
                table: "source_enrollment_codes",
                column: "code_digest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_enrollment_codes_source_id",
                schema: "plant",
                table: "source_enrollment_codes",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "IX_source_security_audit_credential_id",
                schema: "plant",
                table: "source_security_audit",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "IX_source_security_audit_source_id",
                schema: "plant",
                table: "source_security_audit",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "IX_source_security_audit_tenant_id_plant_id_occurred_at_utc",
                schema: "plant",
                table: "source_security_audit",
                columns: new[] { "tenant_id", "plant_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_trusted_sources_tenant_id_plant_id_station_id_status",
                schema: "plant",
                table: "trusted_sources",
                columns: new[] { "tenant_id", "plant_id", "station_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_enrollment_codes",
                schema: "plant");

            migrationBuilder.DropTable(
                name: "source_permissions",
                schema: "plant");

            migrationBuilder.DropTable(
                name: "source_security_audit",
                schema: "plant");

            migrationBuilder.DropTable(
                name: "source_credentials",
                schema: "plant");

            migrationBuilder.DropTable(
                name: "trusted_sources",
                schema: "plant");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Api.Security.ManagementAccess.Migrations
{
    /// <inheritdoc />
    public partial class OrderNotesDefaultPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_membership_permission",
                schema: "identity_access",
                table: "plant_membership_permissions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_membership_permission",
                schema: "identity_access",
                table: "plant_membership_permissions",
                sql: "permission IN ('orders.read', 'orders.create', 'orders.plan.edit', 'orders.notes.default.edit')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_membership_permission",
                schema: "identity_access",
                table: "plant_membership_permissions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_membership_permission",
                schema: "identity_access",
                table: "plant_membership_permissions",
                sql: "permission IN ('orders.read', 'orders.create', 'orders.plan.edit')");
        }
    }
}

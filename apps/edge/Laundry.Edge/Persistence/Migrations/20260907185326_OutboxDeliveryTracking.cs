using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Laundry.Edge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutboxDeliveryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                schema: "plant",
                table: "outbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cloud_received_at_utc",
                schema: "plant",
                table: "outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                schema: "plant",
                table: "outbox",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_id",
                schema: "plant",
                table: "outbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_until_utc",
                schema: "plant",
                table: "outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at_utc",
                schema: "plant",
                table: "outbox",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attempts",
                schema: "plant",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "cloud_received_at_utc",
                schema: "plant",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "last_error",
                schema: "plant",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "lease_id",
                schema: "plant",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "lease_until_utc",
                schema: "plant",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "next_attempt_at_utc",
                schema: "plant",
                table: "outbox");
        }
    }
}

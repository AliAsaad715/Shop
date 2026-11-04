using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shop.Modules.Orders.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_releases",
                schema: "orders",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_releases", x => x.order_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_releases_next_attempt_at",
                schema: "orders",
                table: "pending_releases",
                column: "next_attempt_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_releases",
                schema: "orders");
        }
    }
}

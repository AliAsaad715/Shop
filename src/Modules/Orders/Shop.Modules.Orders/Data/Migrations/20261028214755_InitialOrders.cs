using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shop.Modules.Orders.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orders");

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    total = table.Column<decimal>(type: "numeric(28,2)", precision: 28, scale: 2, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.CheckConstraint("ck_orders_total_nonnegative", "total >= 0");
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(28,2)", precision: 28, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_lines", x => x.id);
                    table.CheckConstraint("ck_order_lines_quantity_positive", "quantity > 0");
                    table.CheckConstraint("ck_order_lines_unit_price_nonnegative", "unit_price >= 0");
                    table.ForeignKey(
                        name: "FK_order_lines_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_status_history",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_status_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_status_history_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_order_id_product_id",
                schema: "orders",
                table: "order_lines",
                columns: new[] { "order_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_status_history_order_id_changed_at",
                schema: "orders",
                table: "order_status_history",
                columns: new[] { "order_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_orders_created_at_id",
                schema: "orders",
                table: "orders",
                columns: new[] { "created_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_orders_customer_id_created_at_id",
                schema: "orders",
                table: "orders",
                columns: new[] { "customer_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_orders_status_created_at_id",
                schema: "orders",
                table: "orders",
                columns: new[] { "status", "created_at", "id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_lines",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "order_status_history",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "orders");
        }
    }
}

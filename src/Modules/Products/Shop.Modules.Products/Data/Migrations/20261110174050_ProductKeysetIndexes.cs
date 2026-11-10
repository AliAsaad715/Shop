using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shop.Modules.Products.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductKeysetIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_products_category_id_created_at_id",
                schema: "products",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_created_at_id",
                schema: "products",
                table: "products");

            migrationBuilder.CreateIndex(
                name: "IX_products_category_id_created_at_id",
                schema: "products",
                table: "products",
                columns: new[] { "category_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_products_created_at_id",
                schema: "products",
                table: "products",
                columns: new[] { "created_at", "id" },
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_products_category_id_created_at_id",
                schema: "products",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_created_at_id",
                schema: "products",
                table: "products");

            migrationBuilder.CreateIndex(
                name: "IX_products_category_id_created_at_id",
                schema: "products",
                table: "products",
                columns: new[] { "category_id", "created_at", "id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_products_created_at_id",
                schema: "products",
                table: "products",
                columns: new[] { "created_at", "id" },
                descending: new[] { true, false });
        }
    }
}

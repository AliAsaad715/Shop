using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Shop.Modules.Products.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "products");

            migrationBuilder.CreateTable(
                name: "categories",
                schema: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    image_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.id);
                    table.CheckConstraint("ck_products_price_nonnegative", "price >= 0");
                    table.ForeignKey(
                        name: "FK_products_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "products",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "products",
                table: "categories",
                columns: new[] { "id", "name" },
                values: new object[,]
                {
                    { new Guid("019a2180-0000-7000-8000-000000000001"), "Electronics" },
                    { new Guid("019a2180-0000-7000-8000-000000000002"), "Clothing" },
                    { new Guid("019a2180-0000-7000-8000-000000000003"), "Home" },
                    { new Guid("019a2180-0000-7000-8000-000000000004"), "Books" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_categories_name",
                schema: "products",
                table: "categories",
                column: "name",
                unique: true);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "products",
                schema: "products");

            migrationBuilder.DropTable(
                name: "categories",
                schema: "products");
        }
    }
}

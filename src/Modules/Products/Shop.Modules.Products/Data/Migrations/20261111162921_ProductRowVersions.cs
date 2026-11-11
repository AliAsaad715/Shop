using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shop.Modules.Products.Data.Migrations
{
    // Maps each row's version to xmin, the system column Postgres keeps on every row (task R3-09). Npgsql writes no
    // SQL for a system column, so this changes only the model: the database already has what it maps
    /// <inheritdoc />
    public partial class ProductRowVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "products",
                table: "products",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "products",
                table: "categories",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "products",
                table: "products");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "products",
                table: "categories");
        }
    }
}

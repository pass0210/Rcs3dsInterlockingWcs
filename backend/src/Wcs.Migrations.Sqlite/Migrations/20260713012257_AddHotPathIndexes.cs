using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddHotPathIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_piece_pid_active",
                table: "piece",
                columns: new[] { "PId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_order_item_barcode",
                table: "order_item",
                column: "Barcode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_piece_pid_active",
                table: "piece");

            migrationBuilder.DropIndex(
                name: "IX_order_item_barcode",
                table: "order_item");
        }
    }
}

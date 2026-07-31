using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class FixPieceIdempotencyIndexExcludeArchived : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_piece_pid_active_status",
                table: "piece");

            migrationBuilder.CreateIndex(
                name: "UQ_piece_pid_active_status",
                table: "piece",
                column: "PId",
                unique: true,
                filter: "\"IsActive\" = 1 AND \"Status\" IN ('DEPOSITED','CELL_ASSIGNED','LOADED') AND \"ArchivedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_piece_pid_active_status",
                table: "piece");

            migrationBuilder.CreateIndex(
                name: "UQ_piece_pid_active_status",
                table: "piece",
                column: "PId",
                unique: true,
                filter: "\"IsActive\" = 1 AND \"Status\" IN ('DEPOSITED','CELL_ASSIGNED','LOADED')");
        }
    }
}

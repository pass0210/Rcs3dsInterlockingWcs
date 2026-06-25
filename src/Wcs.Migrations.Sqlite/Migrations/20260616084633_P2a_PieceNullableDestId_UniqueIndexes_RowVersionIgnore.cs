using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class P2a_PieceNullableDestId_UniqueIndexes_RowVersionIgnore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_piece_destination_DestinationId",
                table: "piece");

            migrationBuilder.DropIndex(
                name: "UQ_piece_pid_is_active",
                table: "piece");

            migrationBuilder.DropIndex(
                name: "IX_cell_assignment_CellId",
                table: "cell_assignment");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "work_batch");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "wcs_order");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "piece");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "order_item");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "destination");

            migrationBuilder.AlterColumn<long>(
                name: "DestinationId",
                table: "piece",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.CreateIndex(
                name: "UQ_piece_pid_active_status",
                table: "piece",
                column: "PId",
                unique: true,
                filter: "\"IsActive\" = 1 AND \"Status\" IN ('DEPOSITED','CELL_ASSIGNED','LOADED')");

            migrationBuilder.CreateIndex(
                name: "UQ_cell_assignment_cell_active",
                table: "cell_assignment",
                column: "CellId",
                unique: true,
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_piece_destination_DestinationId",
                table: "piece",
                column: "DestinationId",
                principalTable: "destination",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_piece_destination_DestinationId",
                table: "piece");

            migrationBuilder.DropIndex(
                name: "UQ_piece_pid_active_status",
                table: "piece");

            migrationBuilder.DropIndex(
                name: "UQ_cell_assignment_cell_active",
                table: "cell_assignment");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "work_batch",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "wcs_order",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "DestinationId",
                table: "piece",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "piece",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "order_item",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "destination",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UQ_piece_pid_is_active",
                table: "piece",
                columns: new[] { "PId", "IsActive" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cell_assignment_CellId",
                table: "cell_assignment",
                column: "CellId");

            migrationBuilder.AddForeignKey(
                name: "FK_piece_destination_DestinationId",
                table: "piece",
                column: "DestinationId",
                principalTable: "destination",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

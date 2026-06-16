using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Migrations.SqlServer.Migrations
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
                name: "UQ_piece_pid_where_active",
                table: "piece");

            migrationBuilder.DropIndex(
                name: "IX_cell_assignment_CellId",
                table: "cell_assignment");

            migrationBuilder.DropColumn(
                name: "XminRowVersion",
                table: "work_batch");

            migrationBuilder.DropColumn(
                name: "XminRowVersion",
                table: "wcs_order");

            migrationBuilder.DropColumn(
                name: "XminRowVersion",
                table: "piece");

            migrationBuilder.DropColumn(
                name: "XminRowVersion",
                table: "order_item");

            migrationBuilder.DropColumn(
                name: "XminRowVersion",
                table: "destination");

            migrationBuilder.AlterColumn<long>(
                name: "DestinationId",
                table: "piece",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.CreateIndex(
                name: "UQ_piece_pid_active_status",
                table: "piece",
                column: "PId",
                unique: true,
                filter: "[IsActive] = 1 AND [Status] IN ('DEPOSITED','CELL_ASSIGNED','LOADED')");

            migrationBuilder.CreateIndex(
                name: "UQ_cell_assignment_cell_active",
                table: "cell_assignment",
                column: "CellId",
                unique: true,
                filter: "[ReleasedAt] IS NULL");

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

            migrationBuilder.AddColumn<int>(
                name: "XminRowVersion",
                table: "work_batch",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "XminRowVersion",
                table: "wcs_order",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<long>(
                name: "DestinationId",
                table: "piece",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "XminRowVersion",
                table: "piece",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "XminRowVersion",
                table: "order_item",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "XminRowVersion",
                table: "destination",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "UQ_piece_pid_where_active",
                table: "piece",
                column: "PId",
                unique: true,
                filter: "[is_active] = 1");

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

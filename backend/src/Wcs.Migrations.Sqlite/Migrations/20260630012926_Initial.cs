using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agv",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AgvNo = table.Column<int>(type: "INTEGER", nullable: false),
                    Floor = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agv", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "destination",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChuteNo = table.Column<int>(type: "INTEGER", nullable: false),
                    DestType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Floor = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    XminRowVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_destination", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "induction",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InductionNo = table.Column<int>(type: "INTEGER", nullable: false),
                    Floor = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_induction", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "plc_event",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Register = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OldVal = table.Column<int>(type: "INTEGER", nullable: true),
                    NewVal = table.Column<int>(type: "INTEGER", nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plc_event", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "printer",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PrinterNo = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ConnInfo = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_printer", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "work_batch",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    BatchNo = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    WaveNo = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    XminRowVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_batch", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cell",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DestinationId = table.Column<long>(type: "INTEGER", nullable: false),
                    CellNo = table.Column<int>(type: "INTEGER", nullable: false),
                    Capacity = table.Column<int>(type: "INTEGER", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cell", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cell_destination_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "destination",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "destination_event",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DestinationId = table.Column<long>(type: "INTEGER", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    DetailJson = table.Column<string>(type: "TEXT", nullable: true),
                    OperatorId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_destination_event", x => x.Id);
                    table.ForeignKey(
                        name: "FK_destination_event_destination_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "destination",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "chute_detail",
                columns: table => new
                {
                    DestinationId = table.Column<long>(type: "INTEGER", nullable: false),
                    DefaultFullQty = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkFullQty = table.Column<int>(type: "INTEGER", nullable: false),
                    PrinterId = table.Column<long>(type: "INTEGER", nullable: true),
                    LastClearedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Zone = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chute_detail", x => x.DestinationId);
                    table.ForeignKey(
                        name: "FK_chute_detail_destination_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "destination",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chute_detail_printer_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "printer",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "wcs_order",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkBatchId = table.Column<long>(type: "INTEGER", nullable: false),
                    OrderNo = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OrderType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RefNo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RefName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DestinationId = table.Column<long>(type: "INTEGER", nullable: true),
                    DestAssignType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    DestAssignedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    XminRowVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wcs_order", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wcs_order_destination_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "destination",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_wcs_order_work_batch_WorkBatchId",
                        column: x => x.WorkBatchId,
                        principalTable: "work_batch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cell_assignment",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CellId = table.Column<long>(type: "INTEGER", nullable: false),
                    OrderId = table.Column<long>(type: "INTEGER", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cell_assignment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cell_assignment_cell_CellId",
                        column: x => x.CellId,
                        principalTable: "cell",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cell_assignment_wcs_order_OrderId",
                        column: x => x.OrderId,
                        principalTable: "wcs_order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_item",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<long>(type: "INTEGER", nullable: false),
                    Barcode = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PlannedQty = table.Column<int>(type: "INTEGER", nullable: false),
                    ReservedQty = table.Column<int>(type: "INTEGER", nullable: false),
                    SortedQty = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    XminRowVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_item", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_item_wcs_order_OrderId",
                        column: x => x.OrderId,
                        principalTable: "wcs_order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "piece",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Barcode = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Qty = table.Column<int>(type: "INTEGER", nullable: false),
                    DepositedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DestinationId = table.Column<long>(type: "INTEGER", nullable: true),
                    OrderItemId = table.Column<long>(type: "INTEGER", nullable: true),
                    AgvId = table.Column<long>(type: "INTEGER", nullable: true),
                    InductionId = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    ClientTs = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    XminRowVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_piece", x => x.Id);
                    table.ForeignKey(
                        name: "FK_piece_agv_AgvId",
                        column: x => x.AgvId,
                        principalTable: "agv",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_piece_destination_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "destination",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_piece_induction_InductionId",
                        column: x => x.InductionId,
                        principalTable: "induction",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_piece_order_item_OrderItemId",
                        column: x => x.OrderItemId,
                        principalTable: "order_item",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "alarm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    PieceId = table.Column<long>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    RaisedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alarm", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alarm_piece_PieceId",
                        column: x => x.PieceId,
                        principalTable: "piece",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "piece_event",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PieceId = table.Column<long>(type: "INTEGER", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    ClientTs = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_piece_event", x => x.Id);
                    table.ForeignKey(
                        name: "FK_piece_event_piece_PieceId",
                        column: x => x.PieceId,
                        principalTable: "piece",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sorter_command",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PieceId = table.Column<long>(type: "INTEGER", nullable: false),
                    CellId = table.Column<long>(type: "INTEGER", nullable: false),
                    CSeq = table.Column<int>(type: "INTEGER", nullable: false),
                    CellNo = table.Column<int>(type: "INTEGER", nullable: false),
                    CWrittenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RSeq = table.Column<int>(type: "INTEGER", nullable: true),
                    RCellNo = table.Column<int>(type: "INTEGER", nullable: true),
                    RFlagAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sorter_command", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sorter_command_cell_CellId",
                        column: x => x.CellId,
                        principalTable: "cell",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sorter_command_piece_PieceId",
                        column: x => x.PieceId,
                        principalTable: "piece",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_agv_agv_no",
                table: "agv",
                column: "AgvNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alarm_acked_at",
                table: "alarm",
                column: "AckedAt");

            migrationBuilder.CreateIndex(
                name: "IX_alarm_PieceId",
                table: "alarm",
                column: "PieceId");

            migrationBuilder.CreateIndex(
                name: "UQ_cell_destination_cell_no",
                table: "cell",
                columns: new[] { "DestinationId", "CellNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cell_assignment_OrderId",
                table: "cell_assignment",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "UQ_cell_assignment_cell_active",
                table: "cell_assignment",
                column: "CellId",
                unique: true,
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_chute_detail_PrinterId",
                table: "chute_detail",
                column: "PrinterId");

            migrationBuilder.CreateIndex(
                name: "UQ_destination_chute_no",
                table: "destination",
                column: "ChuteNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_destination_event_dest_at",
                table: "destination_event",
                columns: new[] { "DestinationId", "At" });

            migrationBuilder.CreateIndex(
                name: "UQ_induction_induction_no",
                table: "induction",
                column: "InductionNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_order_item_order_barcode",
                table: "order_item",
                columns: new[] { "OrderId", "Barcode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_piece_AgvId",
                table: "piece",
                column: "AgvId");

            migrationBuilder.CreateIndex(
                name: "IX_piece_dest_deposited",
                table: "piece",
                columns: new[] { "DestinationId", "DepositedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_piece_dest_status",
                table: "piece",
                columns: new[] { "DestinationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_piece_InductionId",
                table: "piece",
                column: "InductionId");

            migrationBuilder.CreateIndex(
                name: "IX_piece_order_item",
                table: "piece",
                column: "OrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_piece_status",
                table: "piece",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "UQ_piece_pid_active_status",
                table: "piece",
                column: "PId",
                unique: true,
                filter: "\"IsActive\" = 1 AND \"Status\" IN ('DEPOSITED','CELL_ASSIGNED','LOADED')");

            migrationBuilder.CreateIndex(
                name: "IX_piece_event_at",
                table: "piece_event",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_piece_event_piece_at",
                table: "piece_event",
                columns: new[] { "PieceId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_plc_event_at",
                table: "plc_event",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "UQ_printer_printer_no",
                table: "printer",
                column: "PrinterNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sorter_command_CellId",
                table: "sorter_command",
                column: "CellId");

            migrationBuilder.CreateIndex(
                name: "IX_sorter_command_PieceId",
                table: "sorter_command",
                column: "PieceId");

            migrationBuilder.CreateIndex(
                name: "IX_wcs_order_batch_status",
                table: "wcs_order",
                columns: new[] { "WorkBatchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_wcs_order_DestinationId",
                table: "wcs_order",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "UQ_wcs_order_batch_order_no",
                table: "wcs_order",
                columns: new[] { "WorkBatchId", "OrderNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_work_batch_date_batch_wave",
                table: "work_batch",
                columns: new[] { "WorkDate", "BatchNo", "WaveNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alarm");

            migrationBuilder.DropTable(
                name: "cell_assignment");

            migrationBuilder.DropTable(
                name: "chute_detail");

            migrationBuilder.DropTable(
                name: "destination_event");

            migrationBuilder.DropTable(
                name: "piece_event");

            migrationBuilder.DropTable(
                name: "plc_event");

            migrationBuilder.DropTable(
                name: "sorter_command");

            migrationBuilder.DropTable(
                name: "printer");

            migrationBuilder.DropTable(
                name: "cell");

            migrationBuilder.DropTable(
                name: "piece");

            migrationBuilder.DropTable(
                name: "agv");

            migrationBuilder.DropTable(
                name: "induction");

            migrationBuilder.DropTable(
                name: "order_item");

            migrationBuilder.DropTable(
                name: "wcs_order");

            migrationBuilder.DropTable(
                name: "destination");

            migrationBuilder.DropTable(
                name: "work_batch");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Migrations.Sqlite.Migrations
{
    /// <summary>
    /// S-B2B-1: B2B(작업 테스트 데이터) 6테이블 add-only 마이그레이션.
    /// test_data · test_log · work_result · box · box_item · api_call_log. 기존 17테이블 ALTER 0.
    /// ⚠ created_at/called_at 은 B2B 로컬타임(DateTime.Now) — 원본 호환(B2C UTC와 상이 · 사용자 확정 Q3).
    /// box_item→box FK CASCADE(단일 경로). test_log.test_data_id 는 FK 없이 인덱스만.
    /// </summary>
    public partial class AddB2BTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_call_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    endpoint = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    http_method = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    request_body = table.Column<string>(type: "TEXT", nullable: true),
                    response_status = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    response_body = table.Column<string>(type: "TEXT", nullable: true),
                    http_status_code = table.Column<int>(type: "INTEGER", nullable: false),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    client_ip = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    called_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_call_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "box",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    biz_day = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    batch = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    box_no = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    chute_no = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    end_time = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_box", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "test_data",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    biz_day = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    batch = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    barcode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    chute_no = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    receive_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    barcode2 = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_data", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "test_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    log_type = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    biz_day = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    batch = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    barcode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    equipment_no = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    pid = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 5, nullable: true),
                    reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    log_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    test_data_id = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "work_result",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    biz_day = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    batch = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    barcode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    chute_no = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_result", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "box_item",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    box_id = table.Column<long>(type: "INTEGER", nullable: false),
                    barcode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    qty = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_box_item", x => x.id);
                    table.ForeignKey(
                        name: "FK_box_item_box_box_id",
                        column: x => x.box_id,
                        principalTable: "box",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_call_log_called_at",
                table: "api_call_log",
                column: "called_at");

            migrationBuilder.CreateIndex(
                name: "IX_api_call_log_endpoint",
                table: "api_call_log",
                column: "endpoint");

            migrationBuilder.CreateIndex(
                name: "IX_box_biz_day_batch",
                table: "box",
                columns: new[] { "biz_day", "batch" });

            migrationBuilder.CreateIndex(
                name: "IX_box_biz_day_batch_box_no",
                table: "box",
                columns: new[] { "biz_day", "batch", "box_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_box_item_box_id",
                table: "box_item",
                column: "box_id");

            migrationBuilder.CreateIndex(
                name: "IX_test_data_barcode",
                table: "test_data",
                column: "barcode");

            migrationBuilder.CreateIndex(
                name: "IX_test_data_biz_day",
                table: "test_data",
                column: "biz_day");

            migrationBuilder.CreateIndex(
                name: "IX_test_data_biz_day_batch",
                table: "test_data",
                columns: new[] { "biz_day", "batch" });

            migrationBuilder.CreateIndex(
                name: "IX_test_log_barcode",
                table: "test_log",
                column: "barcode");

            migrationBuilder.CreateIndex(
                name: "IX_test_log_biz_day_log_type_log_time",
                table: "test_log",
                columns: new[] { "biz_day", "log_type", "log_time" });

            migrationBuilder.CreateIndex(
                name: "IX_test_log_log_time",
                table: "test_log",
                column: "log_time");

            migrationBuilder.CreateIndex(
                name: "IX_test_log_log_type",
                table: "test_log",
                column: "log_type");

            migrationBuilder.CreateIndex(
                name: "IX_test_log_test_data_id",
                table: "test_log",
                column: "test_data_id");

            migrationBuilder.CreateIndex(
                name: "IX_work_result_biz_day_batch",
                table: "work_result",
                columns: new[] { "biz_day", "batch" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_call_log");

            migrationBuilder.DropTable(
                name: "box_item");

            migrationBuilder.DropTable(
                name: "test_data");

            migrationBuilder.DropTable(
                name: "test_log");

            migrationBuilder.DropTable(
                name: "work_result");

            migrationBuilder.DropTable(
                name: "box");
        }
    }
}

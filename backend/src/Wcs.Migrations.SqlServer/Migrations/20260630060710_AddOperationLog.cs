using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operation_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Level = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    SorterChuteNo = table.Column<int>(type: "int", nullable: true),
                    DestinationId = table.Column<long>(type: "bigint", nullable: true),
                    Barcode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PId = table.Column<int>(type: "int", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operation_log", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_operation_log_at",
                table: "operation_log",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_operation_log_sorter_at",
                table: "operation_log",
                columns: new[] { "SorterChuteNo", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operation_log");
        }
    }
}

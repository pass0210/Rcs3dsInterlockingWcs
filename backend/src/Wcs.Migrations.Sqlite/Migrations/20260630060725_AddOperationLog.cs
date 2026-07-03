using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Migrations.Sqlite.Migrations
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
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    SorterChuteNo = table.Column<int>(type: "INTEGER", nullable: true),
                    DestinationId = table.Column<long>(type: "INTEGER", nullable: true),
                    Barcode = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PId = table.Column<int>(type: "INTEGER", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", nullable: true)
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

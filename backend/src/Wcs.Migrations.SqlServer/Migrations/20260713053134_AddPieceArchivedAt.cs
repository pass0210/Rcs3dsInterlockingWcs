using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPieceArchivedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "sorter_command",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "piece_event",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "piece",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "sorter_command");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "piece_event");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "piece");
        }
    }
}

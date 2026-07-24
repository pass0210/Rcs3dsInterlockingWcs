using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSorterCommandProcessingTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RFlagAt",
                table: "sorter_command",
                newName: "TiltedAt");

            migrationBuilder.AddColumn<DateTime>(
                name: "DepositedAt",
                table: "sorter_command",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReturnedAt",
                table: "sorter_command",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DepositedAt",
                table: "sorter_command");

            migrationBuilder.DropColumn(
                name: "ReturnedAt",
                table: "sorter_command");

            migrationBuilder.RenameColumn(
                name: "TiltedAt",
                table: "sorter_command",
                newName: "RFlagAt");
        }
    }
}

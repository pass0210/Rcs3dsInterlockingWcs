using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSorterCommandSortStartedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SortStartedAt",
                table: "sorter_command",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SortStartedAt",
                table: "sorter_command");
        }
    }
}

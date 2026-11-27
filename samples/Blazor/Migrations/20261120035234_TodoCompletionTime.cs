using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Samples.Blazor.Migrations
{
    /// <inheritdoc />
    public partial class TodoCompletionTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAtUtc",
                table: "todo_items",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "todo_items");
        }
    }
}

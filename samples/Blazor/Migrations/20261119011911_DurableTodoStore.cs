using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Samples.Blazor.Migrations
{
    /// <inheritdoc />
    public partial class DurableTodoStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "todo_scopes",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_todo_scopes", x => x.Scope);
                });

            migrationBuilder.CreateTable(
                name: "todo_items",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    IsCompleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DueDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_todo_items", x => new { x.Scope, x.Id });
                    table.ForeignKey(
                        name: "FK_todo_items_todo_scopes_Scope",
                        column: x => x.Scope,
                        principalTable: "todo_scopes",
                        principalColumn: "Scope",
                        onDelete: ReferentialAction.Cascade);
                });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "todo_items");

            migrationBuilder.DropTable(
                name: "todo_scopes");

        }
    }
}

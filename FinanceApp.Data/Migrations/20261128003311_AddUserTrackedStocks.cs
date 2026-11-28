using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTrackedStocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserTrackedStocks",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    StockId = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(6)")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTrackedStocks", x => new { x.UserId, x.StockId });
                    table.ForeignKey(
                        name: "FK_UserTrackedStocks_Stocks_StockId",
                        column: x => x.StockId,
                        principalTable: "Stocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserTrackedStocks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_UserTrackedStocks_StockId",
                table: "UserTrackedStocks",
                column: "StockId");

            // Existing watchlists were global: preserve them for every user.
            migrationBuilder.Sql(
                "INSERT INTO `UserTrackedStocks` (`UserId`, `StockId`, `CreatedAtUtc`) " +
                "SELECT u.`Id`, s.`Id`, UTC_TIMESTAMP(6) FROM `Users` u " +
                "CROSS JOIN `Stocks` s WHERE s.`TrackingStatus` = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserTrackedStocks");
        }
    }
}

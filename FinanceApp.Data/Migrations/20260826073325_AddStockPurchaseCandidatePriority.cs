using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStockPurchaseCandidatePriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PurchaseCandidatePriority",
                table: "Stocks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Stocks_PurchaseCandidatePriority",
                table: "Stocks",
                column: "PurchaseCandidatePriority");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Stocks_PurchaseCandidatePriority",
                table: "Stocks");

            migrationBuilder.DropColumn(
                name: "PurchaseCandidatePriority",
                table: "Stocks");
        }
    }
}

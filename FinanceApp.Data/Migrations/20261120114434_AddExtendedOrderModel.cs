using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExtendedOrderModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create replacement indexes before dropping the legacy single-column indexes,
            // because MySQL keeps the foreign keys on PortfolioId/StockId anchored to an index.
            migrationBuilder.CreateIndex(
                name: "IX_Orders_PortfolioId_Status",
                table: "Orders",
                columns: new[] { "PortfolioId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_StockId_Status",
                table: "Orders",
                columns: new[] { "StockId", "Status" });

            migrationBuilder.DropIndex(
                name: "IX_Orders_PortfolioId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_StockId",
                table: "Orders");

            migrationBuilder.RenameColumn(
                name: "Type",
                table: "Orders",
                newName: "Side");

            migrationBuilder.AlterColumn<long>(
                name: "Volume",
                table: "StockHistoricalPrices",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<decimal>(
                name: "QuoteUnitMultiplier",
                table: "StockHistoricalPrices",
                type: "decimal(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,6)",
                oldDefaultValue: 1m);

            migrationBuilder.AlterColumn<bool>(
                name: "IsQuoteDerived",
                table: "StockHistoricalPrices",
                type: "tinyint(1)",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "tinyint(1)",
                oldDefaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "AverageFillPrice",
                table: "Orders",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "Orders",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExecutionSource",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "Orders",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FilledQuantity",
                table: "Orders",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Orders",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "OcoGroupId",
                table: "Orders",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "ParentOrderId",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectReason",
                table: "Orders",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "RejectedAt",
                table: "Orders",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StopPrice",
                table: "Orders",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TakeProfitPrice",
                table: "Orders",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimeInForce",
                table: "Orders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "TrailAmount",
                table: "Orders",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TrailPercent",
                table: "Orders",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TrailingReferencePrice",
                table: "Orders",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TriggeredAt",
                table: "Orders",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Orders",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrderEventsOutbox",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    EventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    EventType = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    PortfolioId = table.Column<int>(type: "int", nullable: false),
                    Payload = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderEventsOutbox", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status_Kind",
                table: "Orders",
                columns: new[] { "Status", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderEventsOutbox_EventId",
                table: "OrderEventsOutbox",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderEventsOutbox_ProcessedAt_OccurredAt",
                table: "OrderEventsOutbox",
                columns: new[] { "ProcessedAt", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderEventsOutbox");

            migrationBuilder.DropIndex(
                name: "IX_Orders_PortfolioId_Status",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_Status_Kind",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_StockId_Status",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AverageFillPrice",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ExecutionSource",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "FilledQuantity",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OcoGroupId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ParentOrderId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RejectReason",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RejectedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "StopPrice",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TakeProfitPrice",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TimeInForce",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TrailAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TrailPercent",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TrailingReferencePrice",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TriggeredAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Orders");

            migrationBuilder.RenameColumn(
                name: "Side",
                table: "Orders",
                newName: "Type");

            migrationBuilder.AlterColumn<long>(
                name: "Volume",
                table: "StockHistoricalPrices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<decimal>(
                name: "QuoteUnitMultiplier",
                table: "StockHistoricalPrices",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 1m,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,6)");

            migrationBuilder.AlterColumn<bool>(
                name: "IsQuoteDerived",
                table: "StockHistoricalPrices",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "tinyint(1)");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_PortfolioId",
                table: "Orders",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_StockId",
                table: "Orders",
                column: "StockId");
        }
    }
}

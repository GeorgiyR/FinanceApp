using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEarningsFiscalPeriodEnd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FiscalPeriodEnd",
                table: "EarningsEvents",
                type: "datetime(6)",
                nullable: true);

            // Old history/trend rows stored the fiscal period end in ReportDate.
            // Rows without a fiscal period came from calendarEvents and already hold
            // the actual report date, so leave those untouched.
            migrationBuilder.Sql(
                "UPDATE `EarningsEvents` SET `FiscalPeriodEnd` = `ReportDate`, `ReportDate` = NULL " +
                "WHERE `FiscalPeriodEnd` IS NULL AND `FiscalPeriod` IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FiscalPeriodEnd",
                table: "EarningsEvents");
        }
    }
}

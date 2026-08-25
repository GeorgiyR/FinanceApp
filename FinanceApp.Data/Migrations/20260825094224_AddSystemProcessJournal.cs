using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemProcessJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SystemProcessRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProcessType = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DisplayName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Trigger = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InitiatedByUserId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CorrelationId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExternalRunKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InstanceId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    QueuedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    HeartbeatAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    TotalItems = table.Column<int>(type: "int", nullable: true),
                    ProcessedItems = table.Column<int>(type: "int", nullable: false),
                    SucceededItems = table.Column<int>(type: "int", nullable: false),
                    FailedItems = table.Column<int>(type: "int", nullable: false),
                    SkippedItems = table.Column<int>(type: "int", nullable: false),
                    ResultSummary = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErrorSummary = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastProcessedEntity = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DetailsJson = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemProcessRuns", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_SystemProcessRuns_ActiveLookup",
                table: "SystemProcessRuns",
                columns: new[] { "CompletedAtUtc", "UpdatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemProcessRuns_CorrelationId",
                table: "SystemProcessRuns",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemProcessRuns_ExternalRunKey",
                table: "SystemProcessRuns",
                column: "ExternalRunKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemProcessRuns_ProcessType_QueuedAtUtc_Id",
                table: "SystemProcessRuns",
                columns: new[] { "ProcessType", "QueuedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemProcessRuns_StartedAtUtc_Id",
                table: "SystemProcessRuns",
                columns: new[] { "StartedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemProcessRuns_Status",
                table: "SystemProcessRuns",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SystemProcessRuns");
        }
    }
}

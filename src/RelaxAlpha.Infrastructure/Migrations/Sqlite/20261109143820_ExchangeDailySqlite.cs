using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelaxAlpha.Infrastructure.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ExchangeDailySqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SettlementPrice",
                table: "Quotes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "TradingDate",
                table: "Batches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DailyImports",
                columns: table => new
                {
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TradingDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    LastAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RetryAfterUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Failure = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Futures = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Options = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Contracts = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Fees = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyImports", x => new { x.Source, x.TradingDate });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Batches_TradingDate",
                table: "Batches",
                column: "TradingDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyImports");

            migrationBuilder.DropIndex(
                name: "IX_Batches_TradingDate",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "SettlementPrice",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "TradingDate",
                table: "Batches");
        }
    }
}

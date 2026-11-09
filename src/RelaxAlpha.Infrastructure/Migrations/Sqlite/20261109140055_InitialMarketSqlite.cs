using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelaxAlpha.Infrastructure.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InitialMarketSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Batches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Batches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Quotes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Exchange = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    Underlying = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    ExpiryUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Side = table.Column<int>(type: "INTEGER", nullable: false),
                    Strike = table.Column<decimal>(type: "TEXT", nullable: false),
                    FuturesPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    Multiplier = table.Column<decimal>(type: "TEXT", nullable: false),
                    OpenFee = table.Column<decimal>(type: "TEXT", nullable: false),
                    CloseFee = table.Column<decimal>(type: "TEXT", nullable: false),
                    ExerciseFee = table.Column<decimal>(type: "TEXT", nullable: false),
                    Bid = table.Column<decimal>(type: "TEXT", nullable: true),
                    Ask = table.Column<decimal>(type: "TEXT", nullable: true),
                    Volume = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenInterest = table.Column<int>(type: "INTEGER", nullable: false),
                    Quality = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Quotes_Batches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "Batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Batches_Kind_Source_ObservedAtUtc_ContentHash",
                table: "Batches",
                columns: new[] { "Kind", "Source", "ObservedAtUtc", "ContentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_BatchId",
                table: "Quotes",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_Exchange_Symbol_BatchId",
                table: "Quotes",
                columns: new[] { "Exchange", "Symbol", "BatchId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Quotes");

            migrationBuilder.DropTable(
                name: "Batches");
        }
    }
}

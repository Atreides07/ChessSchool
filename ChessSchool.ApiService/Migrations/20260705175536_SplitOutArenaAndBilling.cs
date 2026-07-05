using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessSchool.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class SplitOutArenaAndBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArenaGames");

            migrationBuilder.DropTable(
                name: "ProcessedBillingEvents");

            migrationBuilder.DropTable(
                name: "Subscriptions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArenaGames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisJson = table.Column<string>(type: "text", nullable: true),
                    BlackIsBot = table.Column<bool>(type: "boolean", nullable: false),
                    BlackName = table.Column<string>(type: "text", nullable: false),
                    BlackSub = table.Column<string>(type: "text", nullable: false),
                    EndReason = table.Column<int>(type: "integer", nullable: false),
                    ExternalGameId = table.Column<string>(type: "text", nullable: false),
                    Pgn = table.Column<string>(type: "text", nullable: false),
                    PlayedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    TournamentId = table.Column<string>(type: "text", nullable: false),
                    WhiteIsBot = table.Column<bool>(type: "boolean", nullable: false),
                    WhiteName = table.Column<string>(type: "text", nullable: false),
                    WhiteSub = table.Column<string>(type: "text", nullable: false),
                    TimeControl_IncrementSeconds = table.Column<int>(type: "integer", nullable: false),
                    TimeControl_InitialSeconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArenaGames", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcessedBillingEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "text", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedBillingEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentPeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Plan = table.Column<string>(type: "text", nullable: true),
                    PriceId = table.Column<string>(type: "text", nullable: true),
                    ProviderCustomerId = table.Column<string>(type: "text", nullable: true),
                    ProviderSubscriptionId = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserSub = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArenaGames_BlackSub_PlayedAt",
                table: "ArenaGames",
                columns: new[] { "BlackSub", "PlayedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArenaGames_ExternalGameId",
                table: "ArenaGames",
                column: "ExternalGameId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArenaGames_WhiteSub_PlayedAt",
                table: "ArenaGames",
                columns: new[] { "WhiteSub", "PlayedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_ProviderSubscriptionId",
                table: "Subscriptions",
                column: "ProviderSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_UserSub",
                table: "Subscriptions",
                column: "UserSub",
                unique: true);
        }
    }
}

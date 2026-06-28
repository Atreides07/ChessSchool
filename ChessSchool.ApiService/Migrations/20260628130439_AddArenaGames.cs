using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessSchool.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class AddArenaGames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArenaGames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TournamentId = table.Column<string>(type: "text", nullable: false),
                    ExternalGameId = table.Column<string>(type: "text", nullable: false),
                    WhiteSub = table.Column<string>(type: "text", nullable: false),
                    BlackSub = table.Column<string>(type: "text", nullable: false),
                    WhiteName = table.Column<string>(type: "text", nullable: false),
                    BlackName = table.Column<string>(type: "text", nullable: false),
                    WhiteIsBot = table.Column<bool>(type: "boolean", nullable: false),
                    BlackIsBot = table.Column<bool>(type: "boolean", nullable: false),
                    Pgn = table.Column<string>(type: "text", nullable: false),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    EndReason = table.Column<int>(type: "integer", nullable: false),
                    TimeControl_InitialSeconds = table.Column<int>(type: "integer", nullable: false),
                    TimeControl_IncrementSeconds = table.Column<int>(type: "integer", nullable: false),
                    PlayedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AnalysisJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArenaGames", x => x.Id);
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArenaGames");
        }
    }
}

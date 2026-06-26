using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessSchool.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Games_BlackStudentId",
                table: "Games",
                column: "BlackStudentId");

            migrationBuilder.CreateIndex(
                name: "IX_Games_Source_PlayedAt",
                table: "Games",
                columns: new[] { "Source", "PlayedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Games_WhiteStudentId",
                table: "Games",
                column: "WhiteStudentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Games_BlackStudentId",
                table: "Games");

            migrationBuilder.DropIndex(
                name: "IX_Games_Source_PlayedAt",
                table: "Games");

            migrationBuilder.DropIndex(
                name: "IX_Games_WhiteStudentId",
                table: "Games");
        }
    }
}

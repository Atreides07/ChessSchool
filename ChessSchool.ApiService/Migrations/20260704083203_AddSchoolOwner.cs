using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessSchool.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class AddSchoolOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerSub",
                table: "Schools",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Schools_OwnerSub",
                table: "Schools",
                column: "OwnerSub");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Schools_OwnerSub",
                table: "Schools");

            migrationBuilder.DropColumn(
                name: "OwnerSub",
                table: "Schools");
        }
    }
}

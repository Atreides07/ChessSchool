using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessSchool.Auth.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Существующим пользователям — уникальная метка (иначе у всех "" и общий стамп). Grandfather.
            migrationBuilder.Sql(
                "UPDATE \"Users\" SET \"SecurityStamp\" = replace(gen_random_uuid()::text, '-', '') WHERE \"SecurityStamp\" = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Users");
        }
    }
}

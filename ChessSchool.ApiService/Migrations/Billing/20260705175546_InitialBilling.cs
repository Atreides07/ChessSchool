using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessSchool.ApiService.Migrations.Billing
{
    /// <inheritdoc />
    public partial class InitialBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    UserSub = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Plan = table.Column<string>(type: "text", nullable: true),
                    ProviderCustomerId = table.Column<string>(type: "text", nullable: true),
                    ProviderSubscriptionId = table.Column<string>(type: "text", nullable: true),
                    PriceId = table.Column<string>(type: "text", nullable: true),
                    CurrentPeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                });

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedBillingEvents");

            migrationBuilder.DropTable(
                name: "Subscriptions");
        }
    }
}

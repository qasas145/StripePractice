using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StripeUseExample.Migrations
{
    /// <inheritdoc />
    public partial class AddFreeTrialAndNullableSubId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "StripeSubscriptionId",
                table: "Users",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.InsertData(
                table: "Plans",
                columns: new[] { "Id", "MonthlyEmailLimit", "Name", "StripePriceId" },
                values: new object[] { 3, 200, "FreeTrial", null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Plans",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.AlterColumn<string>(
                name: "StripeSubscriptionId",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBank.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenCardData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedCardCvv",
                table: "CreditCards");

            migrationBuilder.DropColumn(
                name: "EncryptedCardCvv",
                table: "Accounts");

            migrationBuilder.AddColumn<string>(
                name: "CardNumberHash",
                table: "CreditCards",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditCards_CardNumberHash",
                table: "CreditCards",
                column: "CardNumberHash",
                unique: true,
                filter: "[CardNumberHash] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CreditCards_CardNumberHash",
                table: "CreditCards");

            migrationBuilder.DropColumn(
                name: "CardNumberHash",
                table: "CreditCards");

            migrationBuilder.AddColumn<string>(
                name: "EncryptedCardCvv",
                table: "CreditCards",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EncryptedCardCvv",
                table: "Accounts",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");
        }
    }
}

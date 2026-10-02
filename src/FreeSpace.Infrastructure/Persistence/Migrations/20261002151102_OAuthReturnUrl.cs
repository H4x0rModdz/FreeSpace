using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FreeSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OAuthReturnUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "return_url",
                table: "oauth_states",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "return_url",
                table: "oauth_states");
        }
    }
}

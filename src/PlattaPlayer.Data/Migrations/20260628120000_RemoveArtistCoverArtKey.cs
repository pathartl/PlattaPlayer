using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlattaPlayer.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveArtistCoverArtKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverArtKey",
                table: "Artists");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverArtKey",
                table: "Artists",
                type: "TEXT",
                nullable: true);
        }
    }
}

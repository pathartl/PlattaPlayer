using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlattaPlayer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackSubsong : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Subsong",
                table: "Tracks",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Subsong",
                table: "Tracks");
        }
    }
}

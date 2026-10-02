using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlattaPlayer.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTrackIsMidi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MIDI is played by the MIDI codec plugin now, and its tracks carry the plugin's format name
            // instead of the flag (a rescan writes the same).
            migrationBuilder.Sql("UPDATE Tracks SET Format = 'midi' WHERE IsMidi = 1;");

            migrationBuilder.DropColumn(
                name: "IsMidi",
                table: "Tracks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsMidi",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE Tracks SET IsMidi = 1 WHERE Format = 'midi';");
        }
    }
}

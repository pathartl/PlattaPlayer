using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlattaPlayer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackAudioProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Bitrate",
                table: "Tracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BitsPerSample",
                table: "Tracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SampleRate",
                table: "Tracks",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Bitrate",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "BitsPerSample",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "SampleRate",
                table: "Tracks");
        }
    }
}

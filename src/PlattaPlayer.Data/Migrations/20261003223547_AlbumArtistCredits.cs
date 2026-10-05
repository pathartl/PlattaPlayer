using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlattaPlayer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlbumArtistCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Albums_AlbumArtistId_Title",
                table: "Albums");

            migrationBuilder.AddColumn<string>(
                name: "ArtistCredit",
                table: "Albums",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "AlbumArtistCredits",
                columns: table => new
                {
                    AlbumsId = table.Column<int>(type: "INTEGER", nullable: false),
                    ArtistsId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumArtistCredits", x => new { x.AlbumsId, x.ArtistsId });
                    table.ForeignKey(
                        name: "FK_AlbumArtistCredits_Albums_AlbumsId",
                        column: x => x.AlbumsId,
                        principalTable: "Albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlbumArtistCredits_Artists_ArtistsId",
                        column: x => x.ArtistsId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Existing albums are credited to their one album artist; a rescan splits "A; B" credits.
            migrationBuilder.Sql(
                "UPDATE Albums SET ArtistCredit = (SELECT Name FROM Artists WHERE Artists.Id = Albums.AlbumArtistId);");
            migrationBuilder.Sql(
                "INSERT INTO AlbumArtistCredits (AlbumsId, ArtistsId) SELECT Id, AlbumArtistId FROM Albums;");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_AlbumArtistId",
                table: "Albums",
                column: "AlbumArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_ArtistCredit_Title",
                table: "Albums",
                columns: new[] { "ArtistCredit", "Title" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlbumArtistCredits_ArtistsId",
                table: "AlbumArtistCredits",
                column: "ArtistsId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlbumArtistCredits");

            migrationBuilder.DropIndex(
                name: "IX_Albums_AlbumArtistId",
                table: "Albums");

            migrationBuilder.DropIndex(
                name: "IX_Albums_ArtistCredit_Title",
                table: "Albums");

            migrationBuilder.DropColumn(
                name: "ArtistCredit",
                table: "Albums");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_AlbumArtistId_Title",
                table: "Albums",
                columns: new[] { "AlbumArtistId", "Title" },
                unique: true);
        }
    }
}

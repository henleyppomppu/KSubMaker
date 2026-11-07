using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSubMaker.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds 재생목록: named buckets of jobs, and which jobs each holds. Additive — an upgraded
    /// database starts with no playlists. Entries cascade from both the playlist and the job, so
    /// removing either one never leaves a dangling membership.
    /// </summary>
    public partial class AddPlaylists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Playlists",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playlists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlaylistEntries",
                columns: table => new
                {
                    PlaylistId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    JobId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistEntries", x => new { x.PlaylistId, x.JobId });
                    table.ForeignKey(
                        name: "FK_PlaylistEntries_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaylistEntries_Playlists_PlaylistId",
                        column: x => x.PlaylistId,
                        principalTable: "Playlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistEntries_JobId",
                table: "PlaylistEntries",
                column: "JobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaylistEntries");

            migrationBuilder.DropTable(
                name: "Playlists");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSubMaker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobRemakeRequested : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RemakeRequested",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RemakeRequested",
                table: "Jobs");
        }
    }
}

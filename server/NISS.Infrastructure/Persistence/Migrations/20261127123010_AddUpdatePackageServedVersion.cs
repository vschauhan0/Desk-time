using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NISS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUpdatePackageServedVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "package_served_version",
                table: "device_update_states",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "package_served_version",
                table: "device_update_states");
        }
    }
}

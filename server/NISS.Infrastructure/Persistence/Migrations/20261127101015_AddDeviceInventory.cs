using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NISS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_inventories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    captured_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    manufacturer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    serial = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    os_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    os_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    os_build = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    cpu_model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    cpu_cores = table.Column<int>(type: "integer", nullable: true),
                    cpu_logical = table.Column<int>(type: "integer", nullable: true),
                    ram_gb = table.Column<double>(type: "double precision", nullable: true),
                    disk_total_gb = table.Column<double>(type: "double precision", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    software = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_inventories", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_inventories_device_id",
                table: "device_inventories",
                column: "device_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_inventories");
        }
    }
}

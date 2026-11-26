using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NISS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScreenshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "screenshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_user = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    captured_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    monitor_index = table.Column<int>(type: "integer", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    byte_size = table.Column<int>(type: "integer", nullable: false),
                    rel_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    frame_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_screenshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_screenshots_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_screenshots_captured_at_utc",
                table: "screenshots",
                column: "captured_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_screenshots_device_id_captured_at_utc",
                table: "screenshots",
                columns: new[] { "device_id", "captured_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_screenshots_device_id_frame_id",
                table: "screenshots",
                columns: new[] { "device_id", "frame_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_screenshots_employee_id",
                table: "screenshots",
                column: "employee_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "screenshots");
        }
    }
}

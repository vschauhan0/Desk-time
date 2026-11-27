using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NISS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRollouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_rollouts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    canary_device_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    bake_hours = table.Column<int>(type: "integer", nullable: false),
                    threshold = table.Column<double>(type: "double precision", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    canary_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_rollouts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device_update_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    phase = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    failed_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_update_states", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_update_states_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_rollouts_state",
                table: "agent_rollouts",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_device_update_states_device_id",
                table: "device_update_states",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_update_states_failed_version",
                table: "device_update_states",
                column: "failed_version");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_rollouts");

            migrationBuilder.DropTable(
                name: "device_update_states");
        }
    }
}

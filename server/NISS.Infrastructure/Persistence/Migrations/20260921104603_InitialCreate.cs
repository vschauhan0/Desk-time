using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NISS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "device_number_seq");

            migrationBuilder.CreateTable(
                name: "admin_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_category_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pattern = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_category_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    admin_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    username = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "configuration_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    scope_value = table.Column<string>(type: "text", nullable: false),
                    policy_json = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_configuration_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employees",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_code = table.Column<string>(type: "text", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    department = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_employees", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "enrollment_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    employee_code = table.Column<string>(type: "text", nullable: true),
                    bound_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    used_by_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_enrollment_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "server_settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    admin_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replaced_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_admin_users_admin_user_id",
                        column: x => x.admin_user_id,
                        principalTable: "admin_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "text", nullable: false),
                    hostname = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    os = table.Column<string>(type: "text", nullable: true),
                    os_version = table.Column<string>(type: "text", nullable: true),
                    agent_version = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    machine_guid = table.Column<string>(type: "text", nullable: true),
                    machine_serial = table.Column<string>(type: "text", nullable: true),
                    manufacturer = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    secret_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    secret_rotated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    service_stopped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_boot_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    local_offset_minutes = table.Column<int>(type: "integer", nullable: false),
                    ip_addresses_json = table.Column<string>(type: "jsonb", nullable: true),
                    mac_addresses_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_devices_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "agent_status",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_state = table.Column<string>(type: "text", nullable: false),
                    presence = table.Column<string>(type: "text", nullable: false),
                    user_name = table.Column<string>(type: "text", nullable: true),
                    windows_session_id = table.Column<int>(type: "integer", nullable: true),
                    current_process_id = table.Column<int>(type: "integer", nullable: true),
                    current_application = table.Column<string>(type: "text", nullable: true),
                    current_executable_path = table.Column<string>(type: "text", nullable: true),
                    current_window = table.Column<string>(type: "text", nullable: true),
                    idle_seconds = table.Column<double>(type: "double precision", nullable: false),
                    interacting = table.Column<bool>(type: "boolean", nullable: false),
                    last_input_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cpu_percent = table.Column<double>(type: "double precision", nullable: true),
                    memory_used_percent = table.Column<double>(type: "double precision", nullable: true),
                    disk_used_percent = table.Column<double>(type: "double precision", nullable: true),
                    network_connected = table.Column<bool>(type: "boolean", nullable: true),
                    open_applications_json = table.Column<string>(type: "jsonb", nullable: true),
                    session_agents_json = table.Column<string>(type: "jsonb", nullable: true),
                    collectors_json = table.Column<string>(type: "jsonb", nullable: true),
                    queue_depth = table.Column<int>(type: "integer", nullable: false),
                    dead_letter_count = table.Column<int>(type: "integer", nullable: false),
                    oldest_queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_upload_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_upload_error = table.Column<string>(type: "text", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    clock_offset_ms = table.Column<long>(type: "bigint", nullable: false),
                    config_version = table.Column<int>(type: "integer", nullable: false),
                    server_reachable = table.Column<bool>(type: "boolean", nullable: false),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    live_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_status", x => x.device_id);
                    table.ForeignKey(
                        name: "fk_agent_status_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "application_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    process_id = table.Column<int>(type: "integer", nullable: false),
                    process_name = table.Column<string>(type: "text", nullable: false),
                    executable_path = table.Column<string>(type: "text", nullable: true),
                    window_title = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_seconds = table.Column<double>(type: "double precision", nullable: false),
                    interaction_seconds = table.Column<double>(type: "double precision", nullable: false),
                    idle_seconds = table.Column<double>(type: "double precision", nullable: false),
                    end_reason = table.Column<string>(type: "text", nullable: true),
                    session_type = table.Column<string>(type: "text", nullable: false, defaultValue: "FOREGROUND")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_application_sessions_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    windows_username = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_users_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_users_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "event_ingestion",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    clock_offset_ms = table.Column<long>(type: "bigint", nullable: false),
                    local_offset_minutes = table.Column<int>(type: "integer", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_ingestion", x => x.event_id);
                    table.ForeignKey(
                        name: "fk_event_ingestion_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    old_path = table.Column<string>(type: "text", nullable: true),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    extension = table.Column<string>(type: "text", nullable: true),
                    user_name = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_file_events_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "heartbeats",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    device_timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    agent_version = table.Column<string>(type: "text", nullable: false),
                    uptime_seconds = table.Column<long>(type: "bigint", nullable: false),
                    service_state = table.Column<string>(type: "text", nullable: false),
                    queue_depth = table.Column<int>(type: "integer", nullable: false),
                    clock_offset_ms = table.Column<long>(type: "bigint", nullable: false),
                    cpu_percent = table.Column<double>(type: "double precision", nullable: true),
                    memory_used_percent = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_heartbeats", x => x.id);
                    table.ForeignKey(
                        name: "fk_heartbeats_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "idle_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_seconds = table.Column<double>(type: "double precision", nullable: true),
                    windows_session_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idle_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_idle_sessions_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "input_activity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    bucket_seconds = table.Column<int>(type: "integer", nullable: false),
                    keyboard_activity = table.Column<int>(type: "integer", nullable: false),
                    mouse_activity = table.Column<int>(type: "integer", nullable: false),
                    click_count = table.Column<int>(type: "integer", nullable: false),
                    double_click_count = table.Column<int>(type: "integer", nullable: false),
                    wheel_count = table.Column<int>(type: "integer", nullable: false),
                    last_keyboard_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_mouse_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    optional_cursor_x = table.Column<int>(type: "integer", nullable: true),
                    optional_cursor_y = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_input_activity", x => x.id);
                    table.ForeignKey(
                        name: "fk_input_activity_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "process_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    process_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    process_id = table.Column<int>(type: "integer", nullable: false),
                    parent_process_id = table.Column<int>(type: "integer", nullable: true),
                    process_name = table.Column<string>(type: "text", nullable: false),
                    executable_path = table.Column<string>(type: "text", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_process_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_process_events_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "process_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    process_id = table.Column<int>(type: "integer", nullable: false),
                    parent_process_id = table.Column<int>(type: "integer", nullable: true),
                    process_name = table.Column<string>(type: "text", nullable: false),
                    executable_path = table.Column<string>(type: "text", nullable: true),
                    user_name = table.Column<string>(type: "text", nullable: true),
                    windows_session_id = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    command_line = table.Column<string>(type: "text", nullable: true),
                    sha256 = table.Column<string>(type: "text", nullable: true),
                    baseline = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_process_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_process_sessions_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_security_events_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "system_metrics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cpu_percent = table.Column<double>(type: "double precision", nullable: false),
                    memory_used_percent = table.Column<double>(type: "double precision", nullable: false),
                    memory_total_mb = table.Column<double>(type: "double precision", nullable: false),
                    memory_available_mb = table.Column<double>(type: "double precision", nullable: false),
                    disk_used_percent = table.Column<double>(type: "double precision", nullable: true),
                    disk_free_gb = table.Column<double>(type: "double precision", nullable: true),
                    network_connected = table.Column<bool>(type: "boolean", nullable: true),
                    battery_percent = table.Column<int>(type: "integer", nullable: true),
                    uptime_seconds = table.Column<long>(type: "bigint", nullable: false),
                    disks_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_metrics", x => x.id);
                    table.ForeignKey(
                        name: "fk_system_metrics_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "usb_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    device_type = table.Column<string>(type: "text", nullable: false),
                    vendor_id = table.Column<string>(type: "text", nullable: true),
                    product_id = table.Column<string>(type: "text", nullable: true),
                    vendor = table.Column<string>(type: "text", nullable: true),
                    product = table.Column<string>(type: "text", nullable: true),
                    serial_number = table.Column<string>(type: "text", nullable: true),
                    instance_id = table.Column<string>(type: "text", nullable: true),
                    drive_letter = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usb_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_usb_events_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    windows_session_id = table.Column<int>(type: "integer", nullable: false),
                    user_name = table.Column<string>(type: "text", nullable: true),
                    login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    logout_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    login_missing = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_sessions_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_users_username",
                table: "admin_users",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_app_category_rules_priority",
                table: "app_category_rules",
                column: "priority");

            migrationBuilder.CreateIndex(
                name: "ix_application_sessions_device_id_started_at",
                table: "application_sessions",
                columns: new[] { "device_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_application_sessions_employee_id_started_at",
                table: "application_sessions",
                columns: new[] { "employee_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_application_sessions_open",
                table: "application_sessions",
                column: "device_id",
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_application_sessions_process_name",
                table: "application_sessions",
                column: "process_name");

            migrationBuilder.CreateIndex(
                name: "ix_application_sessions_started_at",
                table: "application_sessions",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_action_timestamp",
                table: "audit_logs",
                columns: new[] { "action", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_timestamp",
                table: "audit_logs",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_configuration_policies_scope_scope_value",
                table: "configuration_policies",
                columns: new[] { "scope", "scope_value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_users_device_id_windows_username",
                table: "device_users",
                columns: new[] { "device_id", "windows_username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_users_employee_id",
                table: "device_users",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_devices_device_id",
                table: "devices",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_employee_id",
                table: "devices",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_devices_machine_guid",
                table: "devices",
                column: "machine_guid",
                unique: true,
                filter: "machine_guid IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_devices_status",
                table: "devices",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_employees_department",
                table: "employees",
                column: "department");

            migrationBuilder.CreateIndex(
                name: "ix_employees_employee_code",
                table: "employees",
                column: "employee_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_enrollment_tokens_expires_at",
                table: "enrollment_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_enrollment_tokens_token_hash",
                table: "enrollment_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_event_ingestion_device_id_event_type_timestamp",
                table: "event_ingestion",
                columns: new[] { "device_id", "event_type", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_event_ingestion_device_id_timestamp",
                table: "event_ingestion",
                columns: new[] { "device_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_event_ingestion_employee_id_timestamp",
                table: "event_ingestion",
                columns: new[] { "employee_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_event_ingestion_timestamp",
                table: "event_ingestion",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_file_events_device_id_timestamp",
                table: "file_events",
                columns: new[] { "device_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_file_events_employee_id",
                table: "file_events",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_file_events_timestamp",
                table: "file_events",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_heartbeats_device_id_timestamp",
                table: "heartbeats",
                columns: new[] { "device_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_heartbeats_timestamp",
                table: "heartbeats",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_idle_sessions_device_id_started_at",
                table: "idle_sessions",
                columns: new[] { "device_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_idle_sessions_employee_id_started_at",
                table: "idle_sessions",
                columns: new[] { "employee_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_idle_sessions_open",
                table: "idle_sessions",
                columns: new[] { "device_id", "kind" },
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_idle_sessions_started_at",
                table: "idle_sessions",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_input_activity_timestamp",
                table: "input_activity",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ux_input_activity_device_bucket",
                table: "input_activity",
                columns: new[] { "device_id", "timestamp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_process_events_device_id_timestamp",
                table: "process_events",
                columns: new[] { "device_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_process_events_timestamp",
                table: "process_events",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_process_sessions_device_id_started_at",
                table: "process_sessions",
                columns: new[] { "device_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_process_sessions_employee_id",
                table: "process_sessions",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_process_sessions_process_name",
                table: "process_sessions",
                column: "process_name");

            migrationBuilder.CreateIndex(
                name: "ix_process_sessions_running",
                table: "process_sessions",
                column: "device_id",
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_process_sessions_started_at",
                table: "process_sessions",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_admin_user_id",
                table: "refresh_tokens",
                column: "admin_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_expires_at",
                table: "refresh_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_security_events_device_id_raised_at",
                table: "security_events",
                columns: new[] { "device_id", "raised_at" });

            migrationBuilder.CreateIndex(
                name: "ix_security_events_open",
                table: "security_events",
                columns: new[] { "device_id", "kind" },
                filter: "resolved_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_security_events_raised_at",
                table: "security_events",
                column: "raised_at");

            migrationBuilder.CreateIndex(
                name: "ix_system_metrics_device_id_timestamp",
                table: "system_metrics",
                columns: new[] { "device_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_system_metrics_timestamp",
                table: "system_metrics",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_usb_events_device_id_timestamp",
                table: "usb_events",
                columns: new[] { "device_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_usb_events_employee_id",
                table: "usb_events",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_usb_events_timestamp",
                table: "usb_events",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_device_id_login_at",
                table: "user_sessions",
                columns: new[] { "device_id", "login_at" });

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_employee_id_login_at",
                table: "user_sessions",
                columns: new[] { "employee_id", "login_at" });

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_login_at",
                table: "user_sessions",
                column: "login_at");

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_open",
                table: "user_sessions",
                column: "device_id",
                filter: "logout_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_status");

            migrationBuilder.DropTable(
                name: "app_category_rules");

            migrationBuilder.DropTable(
                name: "application_sessions");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "configuration_policies");

            migrationBuilder.DropTable(
                name: "device_users");

            migrationBuilder.DropTable(
                name: "enrollment_tokens");

            migrationBuilder.DropTable(
                name: "event_ingestion");

            migrationBuilder.DropTable(
                name: "file_events");

            migrationBuilder.DropTable(
                name: "heartbeats");

            migrationBuilder.DropTable(
                name: "idle_sessions");

            migrationBuilder.DropTable(
                name: "input_activity");

            migrationBuilder.DropTable(
                name: "process_events");

            migrationBuilder.DropTable(
                name: "process_sessions");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "security_events");

            migrationBuilder.DropTable(
                name: "server_settings");

            migrationBuilder.DropTable(
                name: "system_metrics");

            migrationBuilder.DropTable(
                name: "usb_events");

            migrationBuilder.DropTable(
                name: "user_sessions");

            migrationBuilder.DropTable(
                name: "admin_users");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "employees");

            migrationBuilder.DropSequence(
                name: "device_number_seq");
        }
    }
}

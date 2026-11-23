CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    migration_id character varying(150) NOT NULL,
    product_version character varying(32) NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE SEQUENCE device_number_seq START WITH 1 INCREMENT BY 1 NO CYCLE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE admin_users (
        id uuid NOT NULL,
        username text NOT NULL,
        display_name text NOT NULL,
        password_hash text NOT NULL,
        role text NOT NULL,
        enabled boolean NOT NULL,
        created_at timestamp with time zone NOT NULL,
        last_login_at timestamp with time zone,
        CONSTRAINT pk_admin_users PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE app_category_rules (
        id uuid NOT NULL,
        pattern text NOT NULL,
        category text NOT NULL,
        priority integer NOT NULL,
        CONSTRAINT pk_app_category_rules PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE audit_logs (
        id uuid NOT NULL,
        timestamp timestamp with time zone NOT NULL,
        admin_user_id uuid,
        username text,
        action text NOT NULL,
        target text,
        detail text,
        ip_address text,
        CONSTRAINT pk_audit_logs PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE configuration_policies (
        id uuid NOT NULL,
        name text NOT NULL,
        scope text NOT NULL,
        scope_value text NOT NULL,
        policy_json jsonb NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_configuration_policies PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE employees (
        id uuid NOT NULL,
        employee_code text NOT NULL,
        full_name text NOT NULL,
        department text,
        email text,
        active boolean NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_employees PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE enrollment_tokens (
        id uuid NOT NULL,
        token_hash bytea NOT NULL,
        employee_code text,
        bound_device_id uuid,
        expires_at timestamp with time zone NOT NULL,
        used_at timestamp with time zone,
        used_by_device_id uuid,
        note text,
        created_by text,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_enrollment_tokens PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE server_settings (
        key text NOT NULL,
        value text NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_server_settings PRIMARY KEY (key)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE refresh_tokens (
        id uuid NOT NULL,
        admin_user_id uuid NOT NULL,
        token_hash bytea NOT NULL,
        created_at timestamp with time zone NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        revoked_at timestamp with time zone,
        replaced_by_id uuid,
        CONSTRAINT pk_refresh_tokens PRIMARY KEY (id),
        CONSTRAINT fk_refresh_tokens_admin_users_admin_user_id FOREIGN KEY (admin_user_id) REFERENCES admin_users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE devices (
        id uuid NOT NULL,
        device_id text NOT NULL,
        hostname text NOT NULL,
        employee_id uuid,
        os text,
        os_version text,
        agent_version text NOT NULL,
        status text NOT NULL,
        last_seen timestamp with time zone,
        last_event_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        machine_guid text,
        machine_serial text,
        manufacturer text,
        model text,
        secret_hash bytea,
        secret_rotated_at timestamp with time zone,
        service_stopped_at timestamp with time zone,
        last_boot_at timestamp with time zone,
        local_offset_minutes integer NOT NULL,
        ip_addresses_json jsonb,
        mac_addresses_json jsonb,
        CONSTRAINT pk_devices PRIMARY KEY (id),
        CONSTRAINT fk_devices_employees_employee_id FOREIGN KEY (employee_id) REFERENCES employees (id) ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE agent_status (
        device_id uuid NOT NULL,
        service_state text NOT NULL,
        presence text NOT NULL,
        user_name text,
        windows_session_id integer,
        current_process_id integer,
        current_application text,
        current_executable_path text,
        current_window text,
        idle_seconds double precision NOT NULL,
        interacting boolean NOT NULL,
        last_input_at timestamp with time zone,
        cpu_percent double precision,
        memory_used_percent double precision,
        disk_used_percent double precision,
        network_connected boolean,
        open_applications_json jsonb,
        session_agents_json jsonb,
        collectors_json jsonb,
        queue_depth integer NOT NULL,
        dead_letter_count integer NOT NULL,
        oldest_queued_at timestamp with time zone,
        last_upload_at timestamp with time zone,
        last_upload_error text,
        last_error text,
        clock_offset_ms bigint NOT NULL,
        config_version integer NOT NULL,
        server_reachable boolean NOT NULL,
        last_heartbeat_at timestamp with time zone,
        live_updated_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_agent_status PRIMARY KEY (device_id),
        CONSTRAINT fk_agent_status_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE application_sessions (
        id uuid NOT NULL,
        device_id uuid NOT NULL,
        employee_id uuid,
        process_id integer NOT NULL,
        process_name text NOT NULL,
        executable_path text,
        window_title text,
        started_at timestamp with time zone NOT NULL,
        ended_at timestamp with time zone,
        duration_seconds double precision NOT NULL,
        interaction_seconds double precision NOT NULL,
        idle_seconds double precision NOT NULL,
        end_reason text,
        session_type text NOT NULL DEFAULT 'FOREGROUND',
        CONSTRAINT pk_application_sessions PRIMARY KEY (id),
        CONSTRAINT fk_application_sessions_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE device_users (
        id uuid NOT NULL,
        device_id uuid NOT NULL,
        windows_username text NOT NULL,
        employee_id uuid,
        first_seen timestamp with time zone NOT NULL,
        last_seen timestamp with time zone NOT NULL,
        CONSTRAINT pk_device_users PRIMARY KEY (id),
        CONSTRAINT fk_device_users_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE,
        CONSTRAINT fk_device_users_employees_employee_id FOREIGN KEY (employee_id) REFERENCES employees (id) ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE event_ingestion (
        event_id uuid NOT NULL,
        device_id uuid NOT NULL,
        employee_id uuid,
        session_id uuid,
        event_type text NOT NULL,
        timestamp timestamp with time zone NOT NULL,
        received_at timestamp with time zone NOT NULL,
        sequence bigint NOT NULL,
        source text NOT NULL,
        schema_version integer NOT NULL,
        clock_offset_ms bigint NOT NULL,
        local_offset_minutes integer NOT NULL,
        subject text,
        payload jsonb NOT NULL,
        CONSTRAINT pk_event_ingestion PRIMARY KEY (event_id),
        CONSTRAINT fk_event_ingestion_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE file_events (
        id uuid NOT NULL,
        device_id uuid NOT NULL,
        employee_id uuid,
        action text NOT NULL,
        path text NOT NULL,
        old_path text,
        file_name text NOT NULL,
        extension text,
        user_name text,
        timestamp timestamp with time zone NOT NULL,
        CONSTRAINT pk_file_events PRIMARY KEY (id),
        CONSTRAINT fk_file_events_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE heartbeats (
        id uuid NOT NULL,
        device_id uuid NOT NULL,
        timestamp timestamp with time zone NOT NULL,
        device_timestamp timestamp with time zone NOT NULL,
        agent_version text NOT NULL,
        uptime_seconds bigint NOT NULL,
        service_state text NOT NULL,
        queue_depth integer NOT NULL,
        clock_offset_ms bigint NOT NULL,
        cpu_percent double precision,
        memory_used_percent double precision,
        CONSTRAINT pk_heartbeats PRIMARY KEY (id),
        CONSTRAINT fk_heartbeats_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE idle_sessions (
        id uuid NOT NULL,
        device_id uuid NOT NULL,
        employee_id uuid,
        kind text NOT NULL,
        started_at timestamp with time zone NOT NULL,
        ended_at timestamp with time zone,
        duration_seconds double precision,
        windows_session_id integer,
        CONSTRAINT pk_idle_sessions PRIMARY KEY (id),
        CONSTRAINT fk_idle_sessions_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE input_activity (
        id uuid NOT NULL,
        device_id uuid NOT NULL,
        employee_id uuid,
        timestamp timestamp with time zone NOT NULL,
        bucket_seconds integer NOT NULL,
        keyboard_activity integer NOT NULL,
        mouse_activity integer NOT NULL,
        click_count integer NOT NULL,
        double_click_count integer NOT NULL,
        wheel_count integer NOT NULL,
        last_keyboard_at timestamp with time zone,
        last_mouse_at timestamp with time zone,
        optional_cursor_x integer,
        optional_cursor_y integer,
        CONSTRAINT pk_input_activity PRIMARY KEY (id),
        CONSTRAINT fk_input_activity_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE process_events (
        id uuid NOT NULL,
        device_id uuid NOT NULL,
        process_session_id uuid,
        process_id integer NOT NULL,
        parent_process_id integer,
        process_name text NOT NULL,
        executable_path text,
        event_type text NOT NULL,
        timestamp timestamp with time zone NOT NULL,
        CONSTRAINT pk_process_events PRIMARY KEY (id),
        CONSTRAINT fk_process_events_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE process_sessions (
        id uuid NOT NULL,
        device_id uuid NOT NULL,
        employee_id uuid,
        process_id integer NOT NULL,
        parent_process_id integer,
        process_name text NOT NULL,
        executable_path text,
        user_name text,
        windows_session_id integer NOT NULL,
        started_at timestamp with time zone NOT NULL,
        ended_at timestamp with time zone,
        command_line text,
        sha256 text,
        baseline boolean NOT NULL,
        CONSTRAINT pk_process_sessions PRIMARY KEY (id),
        CONSTRAINT fk_process_sessions_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE security_events (
        id uuid NOT NULL,
        device_id uuid,
        kind text NOT NULL,
        severity text NOT NULL,
        message text NOT NULL,
        raised_at timestamp with time zone NOT NULL,
        resolved_at timestamp with time zone,
        CONSTRAINT pk_security_events PRIMARY KEY (id),
        CONSTRAINT fk_security_events_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE system_metrics (
        id uuid NOT NULL,
        device_id uuid NOT NULL,
        timestamp timestamp with time zone NOT NULL,
        cpu_percent double precision NOT NULL,
        memory_used_percent double precision NOT NULL,
        memory_total_mb double precision NOT NULL,
        memory_available_mb double precision NOT NULL,
        disk_used_percent double precision,
        disk_free_gb double precision,
        network_connected boolean,
        battery_percent integer,
        uptime_seconds bigint NOT NULL,
        disks_json jsonb,
        CONSTRAINT pk_system_metrics PRIMARY KEY (id),
        CONSTRAINT fk_system_metrics_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE usb_events (
        id uuid NOT NULL,
        device_id uuid NOT NULL,
        employee_id uuid,
        event_type text NOT NULL,
        device_type text NOT NULL,
        vendor_id text,
        product_id text,
        vendor text,
        product text,
        serial_number text,
        instance_id text,
        drive_letter text,
        timestamp timestamp with time zone NOT NULL,
        CONSTRAINT pk_usb_events PRIMARY KEY (id),
        CONSTRAINT fk_usb_events_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE TABLE user_sessions (
        id uuid NOT NULL,
        device_id uuid NOT NULL,
        employee_id uuid,
        windows_session_id integer NOT NULL,
        user_name text,
        login_at timestamp with time zone NOT NULL,
        logout_at timestamp with time zone,
        login_missing boolean NOT NULL,
        CONSTRAINT pk_user_sessions PRIMARY KEY (id),
        CONSTRAINT fk_user_sessions_devices_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_admin_users_username ON admin_users (username);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_app_category_rules_priority ON app_category_rules (priority);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_application_sessions_device_id_started_at ON application_sessions (device_id, started_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_application_sessions_employee_id_started_at ON application_sessions (employee_id, started_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_application_sessions_open ON application_sessions (device_id) WHERE ended_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_application_sessions_process_name ON application_sessions (process_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_application_sessions_started_at ON application_sessions (started_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_audit_logs_action_timestamp ON audit_logs (action, timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_audit_logs_timestamp ON audit_logs (timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_configuration_policies_scope_scope_value ON configuration_policies (scope, scope_value);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_device_users_device_id_windows_username ON device_users (device_id, windows_username);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_device_users_employee_id ON device_users (employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_devices_device_id ON devices (device_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_devices_employee_id ON devices (employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_devices_machine_guid ON devices (machine_guid) WHERE machine_guid IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_devices_status ON devices (status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_employees_department ON employees (department);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_employees_employee_code ON employees (employee_code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_enrollment_tokens_expires_at ON enrollment_tokens (expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_enrollment_tokens_token_hash ON enrollment_tokens (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_event_ingestion_device_id_event_type_timestamp ON event_ingestion (device_id, event_type, timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_event_ingestion_device_id_timestamp ON event_ingestion (device_id, timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_event_ingestion_employee_id_timestamp ON event_ingestion (employee_id, timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_event_ingestion_timestamp ON event_ingestion (timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_file_events_device_id_timestamp ON file_events (device_id, timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_file_events_employee_id ON file_events (employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_file_events_timestamp ON file_events (timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_heartbeats_device_id_timestamp ON heartbeats (device_id, timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_heartbeats_timestamp ON heartbeats (timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_idle_sessions_device_id_started_at ON idle_sessions (device_id, started_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_idle_sessions_employee_id_started_at ON idle_sessions (employee_id, started_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_idle_sessions_open ON idle_sessions (device_id, kind) WHERE ended_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_idle_sessions_started_at ON idle_sessions (started_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_input_activity_timestamp ON input_activity (timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE UNIQUE INDEX ux_input_activity_device_bucket ON input_activity (device_id, timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_process_events_device_id_timestamp ON process_events (device_id, timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_process_events_timestamp ON process_events (timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_process_sessions_device_id_started_at ON process_sessions (device_id, started_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_process_sessions_employee_id ON process_sessions (employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_process_sessions_process_name ON process_sessions (process_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_process_sessions_running ON process_sessions (device_id) WHERE ended_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_process_sessions_started_at ON process_sessions (started_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_refresh_tokens_admin_user_id ON refresh_tokens (admin_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_refresh_tokens_expires_at ON refresh_tokens (expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_refresh_tokens_token_hash ON refresh_tokens (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_security_events_device_id_raised_at ON security_events (device_id, raised_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_security_events_open ON security_events (device_id, kind) WHERE resolved_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_security_events_raised_at ON security_events (raised_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_system_metrics_device_id_timestamp ON system_metrics (device_id, timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_system_metrics_timestamp ON system_metrics (timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_usb_events_device_id_timestamp ON usb_events (device_id, timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_usb_events_employee_id ON usb_events (employee_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_usb_events_timestamp ON usb_events (timestamp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_user_sessions_device_id_login_at ON user_sessions (device_id, login_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_user_sessions_employee_id_login_at ON user_sessions (employee_id, login_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_user_sessions_login_at ON user_sessions (login_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    CREATE INDEX ix_user_sessions_open ON user_sessions (device_id) WHERE logout_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260921104603_InitialCreate') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20260921104603_InitialCreate', '10.0.12');
    END IF;
END $EF$;
COMMIT;


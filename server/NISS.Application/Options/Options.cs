namespace NISS.Application.Options;

/// <summary>Database settings (<c>Database</c> section).</summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";
    public string ConnectionString { get; set; } = "";
    public bool ApplyMigrationsOnStartup { get; set; } = true;
}

/// <summary>Security settings (<c>Security</c> section).</summary>
public sealed class SecurityOptions
{
    public const string Section = "Security";
    /// <summary>Optional. When empty a random key is generated once and persisted in <c>server_settings</c>.</summary>
    public string JwtSigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenHours { get; set; } = 8;
    public string BootstrapAdminUsername { get; set; } = "admin";
    /// <summary>Optional. When empty and no admin exists, a random password is generated and printed once.</summary>
    public string BootstrapAdminPassword { get; set; } = "";
}

/// <summary>Monitoring defaults (<c>Monitoring</c> section). Used to seed the global policy and the health thresholds.</summary>
public sealed class MonitoringOptions
{
    public const string Section = "Monitoring";
    public int HeartbeatSeconds { get; set; } = 30;
    public int MetricsSeconds { get; set; } = 30;
    public int IdleThresholdMinutes { get; set; } = 5;
    public int HeartbeatWarningSeconds { get; set; } = 60;
    public int OfflineSeconds { get; set; } = 300;
    public int BacklogWarningThreshold { get; set; } = 5000;
    public string MinAgentVersion { get; set; } = "";
}

/// <summary>Retention defaults (<c>Retention</c> section), in days. 0 = keep forever.</summary>
/// <summary>Where periodic screenshots are stored on disk and how long they are kept.</summary>
public sealed class ScreenshotStorageOptions
{
    public const string Section = "ScreenshotStorage";
    /// <summary>Root directory for screenshot files. Empty = a temp directory (dev/test).</summary>
    public string Root { get; set; } = "";
    /// <summary>Default retention for periodic screenshots, in days. Overridden by the Retention settings in the dashboard.</summary>
    public int RetentionDays { get; set; } = 14;
    /// <summary>
    /// Default retention for flagged screenshots and clips, in days. Longer than <see cref="RetentionDays"/> because
    /// flagged captures are evidence. Overridden by the Retention settings in the dashboard.
    /// </summary>
    public int FlaggedRetentionDays { get; set; } = 90;
    /// <summary>Stop capturing/accepting screenshots when the volume has less free space than this (MB). 0 = no guard.</summary>
    public int MinFreeDiskMb { get; set; } = 2048;
}

/// <summary>Live screen view limits.</summary>
public sealed class ScreenLiveOptions
{
    public const string Section = "ScreenLive";
    public int MaxSessionMinutes { get; set; } = 30;
    public int MaxFps { get; set; } = 8;
}

public sealed class RetentionOptions
{
    public const string Section = "Retention";
    public int RawEventsDays { get; set; } = 90;
    public int ApplicationSummariesDays { get; set; } = 365;
    public int DeviceHealthDays { get; set; } = 180;
    public int AuditLogsDays { get; set; } = 365;
}

/// <summary>Rate limits per minute (<c>RateLimits</c> section).</summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";
    public int LoginPerMinute { get; set; } = 5;
    /// <summary>Token refresh (the dashboard refreshes on every page load); refresh tokens are high-entropy so this can be generous.</summary>
    public int RefreshPerMinute { get; set; } = 60;
    public int EnrollPerMinute { get; set; } = 10;
    public int IngestPerMinute { get; set; } = 1200;
}

/// <summary>Background worker settings (<c>Workers</c> section).</summary>
public sealed class WorkerOptions
{
    public const string Section = "Workers";
    public bool Enabled { get; set; } = true;
    public int HealthIntervalSeconds { get; set; } = 15;
    /// <summary>Local hour (0-23) at which the daily retention job runs.</summary>
    public int RetentionHourLocal { get; set; } = 3;
    public int TokenCleanupIntervalMinutes { get; set; } = 60;
}

/// <summary>Agent auto-update settings (<c>AgentUpdate</c> section).</summary>
public sealed class AgentUpdateOptions
{
    public const string Section = "AgentUpdate";
    /// <summary>Root directory for uploaded agent packages. Empty = a temp directory (dev/test).</summary>
    public string Root { get; set; } = "";
    /// <summary>
    /// Publisher pin: Authenticode subjects (e.g. "CN=Acme IT, O=Acme, C=US") accepted for update packages. A file passes
    /// when signed by a certificate whose subject (or a chain issuer's subject) equals ANY entry; list two during a
    /// certificate rotation. Empty rejects every package.
    /// </summary>
    public List<string> AcceptedPublisherSubjects { get; set; } = [];
}

namespace NISS.Monitor.Core.Configuration;

/// <summary>Bound from agentsettings.json (installation directory) and NISS_-prefixed environment variables.</summary>
public sealed class AgentSettings
{
    public const string SectionName = "Agent";

    public ServerSettings Server { get; set; } = new();
    public MonitoringSettings Monitoring { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    public SessionSettings Session { get; set; } = new();
    public EnrollmentSettings Enrollment { get; set; } = new();
    public UpdateSettings Update { get; set; } = new();
}

/// <summary>Optional unattended enrollment. The token is single-use; normally the installer runs <c>NISSAgent --enroll</c> instead.</summary>
public sealed class EnrollmentSettings
{
    public string Token { get; set; } = "";
    public string EmployeeId { get; set; } = "";
}

/// <summary>How to reach the central server.</summary>
public sealed class ServerSettings
{
    /// <summary>e.g. https://192.168.1.222:8443</summary>
    public string BaseUrl { get; set; } = "https://localhost:8443";

    /// <summary>
    /// SHA-256 (preferred) or SHA-1 thumbprint of the server certificate to pin. When empty the normal Windows trust
    /// store is used. Pinning lets a LAN deployment use a self-signed certificate without weakening validation.
    /// </summary>
    public string CertificateThumbprint { get; set; } = "";

    /// <summary>Per-request timeout for uploads/heartbeats.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;
}

/// <summary>Local defaults. The server policy (AgentPolicy) overrides these once received.</summary>
public sealed class MonitoringSettings
{
    public int HeartbeatSeconds { get; set; } = 30;
    public int ForegroundPollMilliseconds { get; set; } = 1000;
    public int SystemMetricsSeconds { get; set; } = 30;
    public int ProcessPollSeconds { get; set; } = 2;
    public int IdleThresholdMinutes { get; set; } = 5;
}

/// <summary>Local storage.</summary>
public sealed class StorageSettings
{
    /// <summary>Overrides C:\ProgramData\NISS\Agent (used by tests and console/dev runs).</summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>Uploaded events are kept locally this long for diagnostics.</summary>
    public int KeepUploadedHours { get; set; } = 24;

    /// <summary>Hard cap on the pending queue; oldest low-value events are dropped beyond it.</summary>
    public int MaxPendingEvents { get; set; } = 1_000_000;
}

/// <summary>Session-agent management.</summary>
public sealed class SessionSettings
{
    /// <summary>The service launches NISSMonitor.Session.exe into each interactive session (needs LocalSystem).</summary>
    public bool LaunchSessionAgents { get; set; } = true;

    /// <summary>Named pipe used between the service and session processes (changeable for tests).</summary>
    public string PipeName { get; set; } = "NISSMonitor.Session";

    /// <summary>Only accept pipe clients whose image is NISSMonitor.Session.exe in the service's own directory.</summary>
    public bool RequireTrustedClientPath { get; set; } = true;

    /// <summary>A session that has not reported for this long is reported as "session agent missing".</summary>
    public int StaleAfterSeconds { get; set; } = 20;
}

/// <summary>Resolves agent file-system locations.</summary>
public sealed class AgentPaths
{
    public AgentPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        LogsDirectory = Path.Combine(dataDirectory, "Logs");
        DatabasePath = Path.Combine(dataDirectory, "agent.db");
        DiagnosticsDirectory = Path.Combine(dataDirectory, "Diagnostics");
    }

    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string DatabasePath { get; }
    public string DiagnosticsDirectory { get; }

    /// <summary>Default C:\ProgramData\NISS\Agent, or the configured override.</summary>
    public static AgentPaths Resolve(AgentSettings settings)
    {
        var overrideDir = Environment.GetEnvironmentVariable("NISS_AGENT_DATA");
        var dir = !string.IsNullOrWhiteSpace(overrideDir) ? overrideDir
            : !string.IsNullOrWhiteSpace(settings.Storage.DataDirectory) ? settings.Storage.DataDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NISS", "Agent");
        return new AgentPaths(dir);
    }

    /// <summary>Create the directories.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(DiagnosticsDirectory);
    }
}

/// <summary>Agent auto-update (read by the watchdog). Updates are refused unless a publisher subject is configured.</summary>
public sealed class UpdateSettings
{
    /// <summary>Accepted Authenticode signer subjects (exact leaf subject, any-of-N for certificate rotation), e.g. "CN=NISS Agent Publisher".</summary>
    public string[] AcceptedPublisherSubjects { get; set; } = [];

    /// <summary>How often the watchdog checks for an assigned target version.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>Maximum random per-device wait before downloading, so a fleet's package pulls spread out.</summary>
    public int DownloadJitterMaxSeconds { get; set; } = 300;

    /// <summary>Dev only: allow a non-TLS download URL.</summary>
    public bool AllowInsecureHttp { get; set; }
}

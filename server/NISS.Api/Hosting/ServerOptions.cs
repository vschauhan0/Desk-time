namespace NISS.Api.Hosting;

/// <summary>TLS certificate settings: a PFX file (Path + Password) or a certificate store thumbprint.</summary>
public sealed class CertificateOptions
{
    public string Path { get; set; } = "";
    public string Password { get; set; } = "";
    public string Thumbprint { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Path) || !string.IsNullOrWhiteSpace(Thumbprint);
}

/// <summary>Listener settings (<c>Server</c> section).</summary>
public sealed class ServerOptions
{
    public const string Section = "Server";
    public string BindIp { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8443;
    public CertificateOptions Certificate { get; set; } = new();
    public bool AllowInsecureHttp { get; set; }
}

/// <summary>Dashboard origin settings (<c>Dashboard</c> section): CORS allows any host on this port.</summary>
public sealed class DashboardOptions
{
    public const string Section = "Dashboard";
    public int Port { get; set; } = 3000;
}

using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace NISS.Api.Hosting;

/// <summary>Kestrel binding: BindIp:Port over HTTPS with the configured certificate; plain HTTP only when explicitly allowed.</summary>
public static class KestrelConfiguration
{
    /// <summary>Maximum request body (batch upload) in bytes.</summary>
    public const long MaxRequestBodyBytes = 5 * 1024 * 1024;

    public static void ConfigureNissKestrel(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            var options = Read(context.Configuration);
            var contentRoot = context.HostingEnvironment.ContentRootPath;
            var certificate = LoadCertificate(options.Certificate, contentRoot);
            var address = ParseAddress(options.BindIp);

            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes;

            if (certificate is not null) kestrel.Listen(address, options.Port, listen => listen.UseHttps(certificate));
            else if (options.AllowInsecureHttp) kestrel.Listen(address, options.Port);
            else throw new InvalidOperationException(NoCertificateMessage);
        });
    }

    public static bool IsPlainHttp(IConfiguration configuration, string contentRoot)
    {
        var options = Read(configuration);
        return !options.Certificate.IsConfigured && options.AllowInsecureHttp;
    }

    private const string NoCertificateMessage =
        "No TLS certificate is configured. Set Server:Certificate:Path (+Password) or Server:Certificate:Thumbprint, " +
        "or - for local development only - set Server:AllowInsecureHttp to true.";

    private static ServerOptions Read(IConfiguration configuration) => configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();

    private static IPAddress ParseAddress(string bindIp) =>
        string.IsNullOrWhiteSpace(bindIp) || bindIp == "0.0.0.0" ? IPAddress.Any
        : bindIp == "::" ? IPAddress.IPv6Any
        : IPAddress.TryParse(bindIp, out var ip) ? ip
        : throw new InvalidOperationException($"Server:BindIp '{bindIp}' is not a valid IP address.");

    private static X509Certificate2? LoadCertificate(CertificateOptions c, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(c.Path))
        {
            var path = Path.IsPathRooted(c.Path) ? c.Path : Path.Combine(contentRoot, c.Path);
            if (!File.Exists(path)) throw new InvalidOperationException($"Server:Certificate:Path '{path}' does not exist.");
            return X509CertificateLoader.LoadPkcs12FromFile(path, string.IsNullOrEmpty(c.Password) ? null : c.Password);
        }
        if (!string.IsNullOrWhiteSpace(c.Thumbprint))
        {
            var thumbprint = c.Thumbprint.Replace(" ", "").Trim();
            foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                if (found.Count > 0) return found[0];
            }
            throw new InvalidOperationException($"No certificate with thumbprint {thumbprint} was found in the LocalMachine or CurrentUser store.");
        }
        return null;
    }
}

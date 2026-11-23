using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NISS.Api.Hosting;

/// <summary>Serilog: console + daily rolling files in <c>logs/</c>, secrets redacted, no request bodies, no query strings.</summary>
public static class LoggingConfiguration
{
    public static void AddNissLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, services, logger) =>
        {
            logger
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                // EF probes the (not yet existing) history table on first start and logs that expected failure as an error.
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Fatal)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.With<RedactionEnricher>()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    Path.Combine(context.HostingEnvironment.ContentRootPath, "logs", "niss-server-.log"),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: 31, shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        });
    }
}

/// <summary>Masks the value of any structured log property whose name looks like a secret (password, token, secret, authorization, key).</summary>
public sealed partial class RedactionEnricher : ILogEventEnricher
{
    [GeneratedRegex("password|passwd|secret|token|authorization|apikey|api_key|signingkey|connectionstring", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var name in logEvent.Properties.Keys.Where(k => SensitiveName().IsMatch(k)).ToList())
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(name, "***"));
    }
}

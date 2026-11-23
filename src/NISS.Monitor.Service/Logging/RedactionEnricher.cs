using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace NISS.Monitor.Service.Logging;

/// <summary>
/// Safety net: any log property whose name looks sensitive (secret, token, password, authorization, key) is masked, and
/// device-credential patterns are scrubbed from string values. The agent already avoids logging such data; this makes
/// an accidental log statement harmless.
/// </summary>
public sealed partial class RedactionEnricher : ILogEventEnricher
{
    [GeneratedRegex("secret|token|password|passwd|authorization|apikey|credential", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    [GeneratedRegex(@"NissDevice\s+\S+|Bearer\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveValue();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            if (SensitiveName().IsMatch(name))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(name, "***"));
            }
            else if (value is ScalarValue { Value: string s } && SensitiveValue().IsMatch(s))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(name, SensitiveValue().Replace(s, "***")));
            }
        }
    }
}

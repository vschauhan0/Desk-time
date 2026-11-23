using System.Globalization;
using System.Text;
using NISS.Application.Common;
using NISS.Application.Reporting;
using NISS.Monitor.Protocol;

namespace NISS.Application.Reports;

/// <summary>Resolved parameters of a report run.</summary>
public sealed record ReportRequest(string Kind, TimeRange Range, Scope Scope, int TzOffsetMinutes, string? Department, string? Application, string? Category);

/// <summary>Produces one kind of tabular report. Reports contain objective telemetry only.</summary>
public interface IReportGenerator
{
    string Kind { get; }
    string Title { get; }
    Task<ReportResult> GenerateAsync(ReportRequest request, CancellationToken ct);
}

/// <summary>Column types understood by the dashboard: string, number, datetime, duration (seconds).</summary>
internal static class Col
{
    public static ReportColumn Str(string key, string label) => new(key, label, "string");
    public static ReportColumn Num(string key, string label) => new(key, label, "number");
    public static ReportColumn Time(string key, string label) => new(key, label, "datetime");
    public static ReportColumn Secs(string key, string label) => new(key, label, "duration");
}

/// <summary>CSV rendering of a <see cref="ReportResult"/> with protection against spreadsheet formula injection.</summary>
public static class ReportCsv
{
    public static string Write(ReportResult report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', report.Columns.Select(c => Escape(c.Label))));
        foreach (var row in report.Rows)
            sb.AppendLine(string.Join(',', report.Columns.Select(c => Escape(Format(row.GetValueOrDefault(c.Key))))));
        return sb.ToString();
    }

    private static string Format(object? v) => v switch
    {
        null => "",
        DateTimeOffset t => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        float f => f.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static string Escape(string s)
    {
        // Cells that a spreadsheet would interpret as a formula are neutralised.
        // A leading '-' is fine for a plain negative number ("-5") but not for "-2+3" or "-cmd|...".
        if (s.Length > 0 && (s[0] is '=' or '+' or '@' or '\t' or '\r'
                             || (s[0] == '-' && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))))
            s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

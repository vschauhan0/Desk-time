using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Application.Common;
using NISS.Application.Reporting;
using NISS.Monitor.Protocol;

namespace NISS.Application.Reports;

/// <summary>Query parameters shared by all reports.</summary>
public sealed record ReportQuery(
    DateTimeOffset? From, DateTimeOffset? To, DateOnly? Date, string? Employee, string? Department, string? Device,
    int? TzOffsetMinutes, string? Application = null, string? Category = null);

/// <summary>Runs reports by kind.</summary>
public interface IReportService
{
    IReadOnlyList<string> Kinds { get; }
    Task<ReportResult> GenerateAsync(string kind, ReportQuery query, CancellationToken ct);
    /// <summary>Generates and renders as CSV; the export is written to the audit log.</summary>
    Task<string> ExportCsvAsync(string kind, ReportQuery query, CancellationToken ct);
}

internal sealed class ReportService(IEnumerable<IReportGenerator> generators, IScopeResolver scopes, TimeProvider clock, IAuditService audit) : IReportService
{
    private readonly Dictionary<string, IReportGenerator> _generators = generators.ToDictionary(g => g.Kind, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Kinds => _generators.Keys.OrderBy(k => k).ToList();

    public async Task<ReportResult> GenerateAsync(string kind, ReportQuery q, CancellationToken ct)
    {
        if (!_generators.TryGetValue(kind.Trim(), out var generator))
            throw new NotFoundException($"Unknown report '{kind}'. Available: {string.Join(", ", Kinds)}.");

        var now = clock.GetUtcNow();
        var tz = q.TzOffsetMinutes ?? TimeRange.ServerOffsetMinutes(now, clock);
        var range = q.Date is { } date ? TimeRange.ForDay(date, tz) : TimeRange.Resolve(q.From, q.To, clock, tz);
        var scope = await scopes.ResolveAsync(q.Device, q.Employee, q.Department, ct);
        return await generator.GenerateAsync(new ReportRequest(generator.Kind, range, scope, tz, q.Department, q.Application, q.Category), ct);
    }

    public async Task<string> ExportCsvAsync(string kind, ReportQuery query, CancellationToken ct)
    {
        var report = await GenerateAsync(kind, query, ct);
        await audit.WriteAsync(AuditActions.ReportExported, report.Kind, $"{report.FromUtc:u} .. {report.ToUtc:u}, {report.Rows.Count} rows", ct);
        return ReportCsv.Write(report);
    }
}

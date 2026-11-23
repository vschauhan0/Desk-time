using NISS.Application.Common;

namespace NISS.Api.Features;

// Query-string models bound with [AsParameters]. Property names are the (case-insensitive) query keys.

public sealed record PageQuery(int? Page, int? PageSize)
{
    public PageRequest Request => PageRequest.From(Page, PageSize);
}

public sealed record DeviceListQuery(string? Status, string? Employee, string? Department, string? Search, int? Page, int? PageSize)
{
    public PageRequest Paging => PageRequest.From(Page, PageSize);
}

public sealed record LiveQuery(string? Status, string? Department, string? Employee, string? Search);

public sealed record ActivityQuery(DateTimeOffset? From, DateTimeOffset? To, string[]? EventType, int? Page, int? PageSize)
{
    public PageRequest Paging => PageRequest.From(Page, PageSize);
}

public sealed record ApplicationsQuery(
    DateTimeOffset? From, DateTimeOffset? To, string? Application, string? Employee, string? Department, string? Device, string? Category,
    int? Page, int? PageSize)
{
    public PageRequest Paging => PageRequest.From(Page, PageSize);
}

public sealed record ProcessesQuery(
    DateTimeOffset? From, DateTimeOffset? To, string? Status, string? Search, string? Employee, string? Department, string? Device,
    int? Page, int? PageSize)
{
    public PageRequest Paging => PageRequest.From(Page, PageSize);
}

public sealed record MetricsQuery(DateTimeOffset? From, DateTimeOffset? To, int? MaxPoints);

public sealed record RangeQuery(DateTimeOffset? From, DateTimeOffset? To);

public sealed record EmployeeListQuery(string? Search, string? Department, int? Page, int? PageSize)
{
    public PageRequest Paging => PageRequest.From(Page, PageSize);
}

public sealed record SummaryQuery(DateOnly? Date, int? TzOffsetMinutes);

public sealed record SecurityEventsQuery(
    DateTimeOffset? From, DateTimeOffset? To, string? Device, string? Kind, string? Severity, bool? Resolved, int? Page, int? PageSize)
{
    public PageRequest Paging => PageRequest.From(Page, PageSize);
}

public sealed record AuditLogQuery(DateTimeOffset? From, DateTimeOffset? To, string? User, string? Action, int? Page, int? PageSize)
{
    public PageRequest Paging => PageRequest.From(Page, PageSize);
}

public sealed record UsbQuery(
    DateTimeOffset? From, DateTimeOffset? To, string? Device, string? Employee, string? Department, string? Action, int? Page, int? PageSize)
{
    public PageRequest Paging => PageRequest.From(Page, PageSize);
}

public sealed record FileQuery(
    DateTimeOffset? From, DateTimeOffset? To, string? Device, string? Employee, string? Department, string? Action, string? Search, int? Page, int? PageSize)
{
    public PageRequest Paging => PageRequest.From(Page, PageSize);
}

public sealed record ReportHttpQuery(
    DateTimeOffset? From, DateTimeOffset? To, DateOnly? Date, string? Employee, string? Department, string? Device, int? TzOffsetMinutes,
    string? Format, string? Application, string? Category);

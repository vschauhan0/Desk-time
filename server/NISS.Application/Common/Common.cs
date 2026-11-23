using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NISS.Monitor.Protocol;

namespace NISS.Application.Common;

/// <summary>An error that maps to an HTTP problem-details response.</summary>
public class AppException(int statusCode, string title, string? detail = null, IDictionary<string, string[]>? errors = null)
    : Exception(detail ?? title)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public IDictionary<string, string[]>? Errors { get; } = errors;
}

/// <summary>404.</summary>
public sealed class NotFoundException(string detail) : AppException(404, "Not found", detail);

/// <summary>409.</summary>
public sealed class ConflictException(string detail) : AppException(409, "Conflict", detail);

/// <summary>403.</summary>
public sealed class ForbiddenException(string detail) : AppException(403, "Forbidden", detail);

/// <summary>401.</summary>
public sealed class UnauthorizedAppException(string detail) : AppException(401, "Unauthorized", detail);

/// <summary>400 with a single field error.</summary>
public sealed class BadRequestException(string detail, string? field = null)
    : AppException(400, "Bad request", detail, field is null ? null : new Dictionary<string, string[]> { [field] = [detail] });

/// <summary>Normalised paging parameters.</summary>
public readonly record struct PageRequest(int Page, int PageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;

    public static PageRequest From(int? page, int? pageSize) =>
        new(Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));

    public int Skip => (Page - 1) * PageSize;
}

/// <summary>Query helpers.</summary>
public static class QueryExtensions
{
    /// <summary>Counts, then reads one page of an ordered query and maps it.</summary>
    public static async Task<Paged<TOut>> ToPagedAsync<TIn, TOut>(
        this IQueryable<TIn> query, PageRequest page, Func<IReadOnlyList<TIn>, Task<IReadOnlyList<TOut>>> map, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var rows = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(ct);
        return new Paged<TOut>(await map(rows), total, page.Page, page.PageSize);
    }

    /// <summary>Synchronous-mapping overload.</summary>
    public static Task<Paged<TOut>> ToPagedAsync<TIn, TOut>(
        this IQueryable<TIn> query, PageRequest page, Func<TIn, TOut> map, CancellationToken ct) =>
        query.ToPagedAsync(page, rows => Task.FromResult<IReadOnlyList<TOut>>(rows.Select(map).ToList()), ct);

    /// <summary>Escapes LIKE wildcards and wraps the value for a "contains" match.</summary>
    public static string LikeContains(string value) =>
        "%" + value.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}

/// <summary>JSON helpers for jsonb text columns.</summary>
public static class JsonColumn
{
    public static string? Serialize<T>(T? value) where T : class => value is null ? null : JsonSerializer.Serialize(value, ProtocolJson.Options);

    public static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, ProtocolJson.Options); }
        catch (JsonException) { return null; }
    }

    public static IReadOnlyList<T> DeserializeList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<T>>(json, ProtocolJson.Options) ?? []; }
        catch (JsonException) { return []; }
    }

    public static JsonElement ToElement(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
    }
}

/// <summary>A closed-open UTC time range used by queries.</summary>
public readonly record struct TimeRange(DateTimeOffset From, DateTimeOffset To)
{
    public double Seconds => (To - From).TotalSeconds;

    /// <summary>The range for a local calendar day given as <c>yyyy-MM-dd</c> in the supplied UTC offset.</summary>
    public static TimeRange ForDay(DateOnly date, int tzOffsetMinutes)
    {
        var offset = TimeSpan.FromMinutes(tzOffsetMinutes);
        var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), offset).ToUniversalTime();
        return new TimeRange(start, start.AddDays(1));
    }

    /// <summary>Server-local UTC offset in minutes for the given instant.</summary>
    public static int ServerOffsetMinutes(DateTimeOffset at, TimeProvider clock) =>
        (int)clock.LocalTimeZone.GetUtcOffset(at).TotalMinutes;

    /// <summary>Resolves from/to query values. Default range is "today" in the server's local time zone.</summary>
    public static TimeRange Resolve(DateTimeOffset? from, DateTimeOffset? to, TimeProvider clock, int? tzOffsetMinutes = null)
    {
        var now = clock.GetUtcNow();
        var offset = tzOffsetMinutes ?? ServerOffsetMinutes(now, clock);
        var local = now.ToOffset(TimeSpan.FromMinutes(offset));
        var today = TimeRange.ForDay(DateOnly.FromDateTime(local.DateTime), offset);
        var f = (from ?? today.From).ToUniversalTime();
        var t = (to ?? (from is null ? today.To : now)).ToUniversalTime();
        if (t <= f) t = f.AddDays(1);
        return new TimeRange(f, t);
    }
}

/// <summary>Small string helpers.</summary>
public static class Text
{
    public static string? Trunc(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    /// <summary>Process name without the ".exe" suffix, used as grouping/categorisation key.</summary>
    public static string NormalizeProcess(string name)
    {
        var n = name.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }
}

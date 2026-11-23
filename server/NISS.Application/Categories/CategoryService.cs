using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Monitor.Protocol;
using DomainRule = NISS.Domain.Entities.AppCategoryRule;
using RuleDto = NISS.Monitor.Protocol.AppCategoryRule;

namespace NISS.Application.Categories;

/// <summary>Application categories used by reports.</summary>
public static class AppCategories
{
    public const string Developer = "Developer";
    public const string Office = "Office";
    public const string Communication = "Communication";
    public const string Browser = "Browser";
    public const string Media = "Media";
    public const string Utilities = "Utilities";
    public const string System = "System";
    public const string Unknown = "Unknown";

    public static readonly IReadOnlyList<string> All = [Developer, Office, Communication, Browser, Media, Utilities, System, Unknown];
}

/// <summary>Seed data for the categorisation rules.</summary>
public static class DefaultCategoryRules
{
    public static IReadOnlyList<(string Pattern, string Category)> Rules { get; } =
    [
        ("code*", AppCategories.Developer), ("devenv*", AppCategories.Developer), ("rider*", AppCategories.Developer),
        ("idea*", AppCategories.Developer), ("pycharm*", AppCategories.Developer), ("git*", AppCategories.Developer),
        ("excel*", AppCategories.Office), ("winword*", AppCategories.Office), ("powerpnt*", AppCategories.Office),
        ("outlook*", AppCategories.Office), ("onenote*", AppCategories.Office), ("acrord*", AppCategories.Office),
        ("teams*", AppCategories.Communication), ("slack*", AppCategories.Communication), ("zoom*", AppCategories.Communication),
        ("discord*", AppCategories.Communication), ("skype*", AppCategories.Communication),
        ("chrome*", AppCategories.Browser), ("msedge*", AppCategories.Browser), ("firefox*", AppCategories.Browser),
        ("brave*", AppCategories.Browser), ("opera*", AppCategories.Browser),
        ("vlc*", AppCategories.Media), ("spotify*", AppCategories.Media), ("wmplayer*", AppCategories.Media),
        ("notepad*", AppCategories.Utilities), ("calc*", AppCategories.Utilities), ("explorer*", AppCategories.Utilities),
        ("7z*", AppCategories.Utilities), ("winrar*", AppCategories.Utilities),
        ("svchost", AppCategories.System), ("lsass", AppCategories.System), ("csrss", AppCategories.System),
        ("dwm", AppCategories.System), ("taskmgr", AppCategories.System), ("searchhost", AppCategories.System),
        ("services", AppCategories.System), ("wininit", AppCategories.System), ("winlogon", AppCategories.System),
        ("system", AppCategories.System), ("smss", AppCategories.System), ("fontdrvhost", AppCategories.System),
        ("sihost", AppCategories.System), ("ctfmon", AppCategories.System), ("runtimebroker", AppCategories.System),
        ("startmenuexperiencehost", AppCategories.System), ("shellexperiencehost", AppCategories.System),
        ("applicationframehost", AppCategories.System), ("textinputhost", AppCategories.System),
        ("securityhealth*", AppCategories.System), ("msmpeng", AppCategories.System), ("conhost", AppCategories.System),
        ("lockapp", AppCategories.System), ("logonui", AppCategories.System), ("niss*", AppCategories.System),
    ];
}

/// <summary>Immutable snapshot of the rules that classifies process names.</summary>
public sealed class CategoryMatcher(IEnumerable<DomainRule> rules)
{
    private readonly (string Pattern, string Category)[] _rules = rules
        .OrderByDescending(r => r.Priority)
        .ThenByDescending(r => r.Pattern.Length)
        .Select(r => (Text.NormalizeProcess(r.Pattern), r.Category))
        .ToArray();

    /// <summary>Category of a process name (Unknown when nothing matches).</summary>
    public string Categorize(string processName)
    {
        var name = Text.NormalizeProcess(processName);
        foreach (var (pattern, category) in _rules)
            if (WildcardMatch(pattern, name)) return category;
        return AppCategories.Unknown;
    }

    /// <summary>Case-insensitive glob match supporting <c>*</c> and <c>?</c>.</summary>
    public static bool WildcardMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

/// <summary>Category rule access and administration.</summary>
public interface ICategoryService
{
    Task<CategoryMatcher> GetMatcherAsync(CancellationToken ct);
    Task<IReadOnlyList<RuleDto>> ListAsync(CancellationToken ct);
    Task<IReadOnlyList<RuleDto>> ReplaceAsync(IReadOnlyList<RuleDto> rules, CancellationToken ct);
}

internal sealed class CategoryService(IAppDbContext db, IMemoryCache cache, IAuditService audit) : ICategoryService
{
    private const string CacheKey = "category-matcher";

    public async Task<CategoryMatcher> GetMatcherAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out CategoryMatcher? m) && m is not null) return m;
        var rules = await db.AppCategoryRules.AsNoTracking().ToListAsync(ct);
        m = new CategoryMatcher(rules);
        cache.Set(CacheKey, m, TimeSpan.FromMinutes(1));
        return m;
    }

    public async Task<IReadOnlyList<RuleDto>> ListAsync(CancellationToken ct) =>
        await db.AppCategoryRules.AsNoTracking().OrderByDescending(r => r.Priority).ThenBy(r => r.Pattern)
            .Select(r => new RuleDto(r.Id, r.Pattern, r.Category, r.Priority)).ToListAsync(ct);

    public async Task<IReadOnlyList<RuleDto>> ReplaceAsync(
        IReadOnlyList<RuleDto> rules, CancellationToken ct)
    {
        var existing = await db.AppCategoryRules.ToListAsync(ct);
        var keep = new HashSet<Guid>();
        foreach (var r in rules)
        {
            var row = r.Id == Guid.Empty ? null : existing.FirstOrDefault(e => e.Id == r.Id);
            if (row is null)
            {
                db.AppCategoryRules.Add(new DomainRule { Id = Guid.NewGuid(), Pattern = r.Pattern.Trim(), Category = r.Category, Priority = r.Priority });
            }
            else { row.Pattern = r.Pattern.Trim(); row.Category = r.Category; row.Priority = r.Priority; keep.Add(row.Id); }
        }
        db.AppCategoryRules.RemoveRange(existing.Where(e => !keep.Contains(e.Id)));
        audit.Record("APP_CATEGORIES_UPDATED", null, $"{rules.Count} rules");
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
        return await ListAsync(ct);
    }
}

using NISS.Application.Categories;
using NISS.Application.Common;
using NISS.Application.Reporting;
using NISS.Application.Reports;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;
using NISS.Workers;
using DomainRule = NISS.Domain.Entities.AppCategoryRule;

namespace NISS.Api.Tests;

/// <summary>Pure logic that needs neither a database nor a host.</summary>
public sealed class IntervalMathTests
{
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static Interval I(int startMin, int endMin) => new(T.AddMinutes(startMin), T.AddMinutes(endMin));

    [Fact]
    public void Normalize_merges_overlapping_touching_and_unsorted_intervals_and_drops_empty_ones()
    {
        var result = IntervalMath.Normalize([I(30, 40), I(0, 10), I(5, 20), I(20, 25), I(50, 50), I(60, 55)]);
        Assert.Equal([I(0, 25), I(30, 40)], result);
    }

    [Fact]
    public void Clip_restricts_to_the_window()
    {
        var result = IntervalMath.Clip([I(-10, 5), I(8, 12), I(20, 30)], I(0, 10));
        Assert.Equal([I(0, 5), I(8, 10)], result);
    }

    [Fact]
    public void Intersect_returns_the_common_parts()
    {
        var result = IntervalMath.Intersect([I(0, 10), I(20, 30)], [I(5, 25)]);
        Assert.Equal([I(5, 10), I(20, 25)], result);
        Assert.Empty(IntervalMath.Intersect([I(0, 5)], [I(5, 10)]));
    }

    [Fact]
    public void Subtract_removes_covered_parts_including_holes()
    {
        Assert.Equal([I(0, 3), I(6, 10)], IntervalMath.Subtract([I(0, 10)], [I(3, 6)]));
        Assert.Equal([I(0, 2), I(4, 6), I(8, 10)], IntervalMath.Subtract([I(0, 10)], [I(2, 4), I(6, 8)]));
        Assert.Empty(IntervalMath.Subtract([I(2, 4)], [I(0, 10)]));
        Assert.Equal([I(0, 10)], IntervalMath.Subtract([I(0, 10)], []));
        Assert.Equal([I(5, 10)], IntervalMath.Subtract([I(0, 10)], [I(-5, 5)]));
    }

    [Fact]
    public void Total_seconds_sums_lengths()
    {
        Assert.Equal(15 * 60, IntervalMath.TotalSeconds([I(0, 10), I(20, 25)]));
    }
}

public sealed class PresenceCalculatorTests
{
    private static readonly DateTimeOffset Day = new(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeRange Window = new(Day, Day.AddDays(1));
    private static readonly Guid Dev = Guid.NewGuid();
    private static DateTimeOffset At(int h, int m = 0) => Day.AddHours(h).AddMinutes(m);

    [Fact]
    public void Active_is_logged_in_minus_idle_locked_and_disconnected_with_overlaps_merged()
    {
        var logins = new[] { new LoginRow(Dev, null, At(9), At(17), null) };
        var idles = new[]
        {
            new IdleRow(Dev, null, IdleKinds.Idle, At(10), At(10, 30), null),
            new IdleRow(Dev, null, IdleKinds.Idle, At(10, 20), At(11), null),          // overlaps the first idle period
            new IdleRow(Dev, null, IdleKinds.Locked, At(10, 50), At(11, 30), null),    // locked wins over the idle part it overlaps
            new IdleRow(Dev, null, IdleKinds.Disconnected, At(13), At(14), null),
            new IdleRow(Dev, null, IdleKinds.Idle, At(18), At(19), null),              // after logout: irrelevant
        };

        var p = PresenceCalculator.Compute(logins, idles, Window, At(20));
        Assert.Equal(8 * 3600, p.LoggedInSeconds);
        Assert.Equal(100 * 60, p.LockedSeconds);       // 10:50-11:30 (40 min) + 13:00-14:00 (60 min)
        Assert.Equal(50 * 60, p.IdleSeconds);          // 10:00-10:50 (idle ends where lock begins)
        Assert.Equal(8 * 3600 - 100 * 60 - 50 * 60, p.ActiveSeconds);
        Assert.Equal(p.LoggedInSeconds, p.ActiveSeconds + p.IdleSeconds + p.LockedSeconds);
    }

    [Fact]
    public void Open_sessions_end_now_but_not_after_the_device_was_last_seen()
    {
        var now = At(12);
        Assert.Equal(3 * 3600, PresenceCalculator.Compute([new LoginRow(Dev, null, At(9), null, null)], [], Window, now).LoggedInSeconds);
        Assert.Equal(1 * 3600, PresenceCalculator.Compute([new LoginRow(Dev, null, At(9), null, At(10))], [], Window, now).LoggedInSeconds);   // device vanished at 10:00
    }

    [Fact]
    public void Everything_is_clipped_to_the_window()
    {
        var window = new TimeRange(At(10), At(12));
        var p = PresenceCalculator.Compute([new LoginRow(Dev, null, At(8), At(20), null)], [new IdleRow(Dev, null, IdleKinds.Idle, At(9), At(10, 30), null)], window, At(23));
        Assert.Equal(2 * 3600, p.LoggedInSeconds);
        Assert.Equal(30 * 60, p.IdleSeconds);
        Assert.Equal(90 * 60, p.ActiveSeconds);
    }

    [Fact]
    public void Idle_without_a_logged_in_session_is_ignored()
    {
        var p = PresenceCalculator.Compute([], [new IdleRow(Dev, null, IdleKinds.Idle, At(9), At(10), null)], Window, At(23));
        Assert.Equal(0, p.LoggedInSeconds + p.IdleSeconds + p.ActiveSeconds);
    }

    [Fact]
    public void Two_devices_of_one_employee_do_not_double_count_overlapping_logins()
    {
        var other = Guid.NewGuid();
        var p = PresenceCalculator.Compute([new LoginRow(Dev, null, At(9), At(12), null), new LoginRow(other, null, At(11), At(14), null)], [], Window, At(23));
        Assert.Equal(5 * 3600, p.LoggedInSeconds);
    }
}

public sealed class CategoryMatcherTests
{
    private static CategoryMatcher Default() =>
        new(DefaultCategoryRules.Rules.Select(r => new DomainRule { Pattern = r.Pattern, Category = r.Category, Priority = 100 }));

    [Theory]
    [InlineData("chrome.exe", "Browser")]
    [InlineData("CHROME", "Browser")]
    [InlineData("msedge.exe", "Browser")]
    [InlineData("firefox", "Browser")]
    [InlineData("EXCEL.EXE", "Office")]
    [InlineData("WINWORD.EXE", "Office")]
    [InlineData("Code.exe", "Developer")]
    [InlineData("devenv.exe", "Developer")]
    [InlineData("rider64.exe", "Developer")]
    [InlineData("Teams.exe", "Communication")]
    [InlineData("slack.exe", "Communication")]
    [InlineData("vlc.exe", "Media")]
    [InlineData("notepad.exe", "Utilities")]
    [InlineData("7zFM.exe", "Utilities")]
    [InlineData("svchost.exe", "System")]
    [InlineData("lsass.exe", "System")]
    [InlineData("dwm", "System")]
    [InlineData("totally-unknown.exe", "Unknown")]
    [InlineData("svchost-lookalike.exe", "Unknown")]   // exact patterns do not match prefixes
    public void Default_rules_classify_common_applications(string process, string expected) => Assert.Equal(expected, Default().Categorize(process));

    [Fact]
    public void Higher_priority_and_longer_patterns_win()
    {
        var matcher = new CategoryMatcher(
        [
            new DomainRule { Pattern = "chrome*", Category = "Browser", Priority = 10 },
            new DomainRule { Pattern = "chromeremote*", Category = "Utilities", Priority = 10 },
            new DomainRule { Pattern = "*", Category = "Office", Priority = 0 },
            new DomainRule { Pattern = "chrome_special.exe", Category = "Media", Priority = 50 },
        ]);
        Assert.Equal("Utilities", matcher.Categorize("chromeremotedesktop.exe"));   // longer pattern at equal priority
        Assert.Equal("Media", matcher.Categorize("chrome_special.exe"));            // higher priority
        Assert.Equal("Browser", matcher.Categorize("chrome.exe"));
        Assert.Equal("Office", matcher.Categorize("anything"));
    }

    [Theory]
    [InlineData("chrome*", "chrome", true)]
    [InlineData("chrome*", "chromium", false)]
    [InlineData("*studio", "visualstudio", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("*", "", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("", "", true)]
    public void Wildcards(string pattern, string text, bool expected) => Assert.Equal(expected, CategoryMatcher.WildcardMatch(pattern, text));
}

public sealed class ReportCsvTests
{
    [Fact]
    public void Csv_neutralises_formula_injection_and_escapes_delimiters()
    {
        var report = new ReportResult("test", "Test", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1),
            [new ReportColumn("a", "Name", "string"), new ReportColumn("n", "Number", "number"), new ReportColumn("t", "Time", "datetime")],
            [
                new() { ["a"] = "=HYPERLINK(\"http://evil\")", ["n"] = 1.5, ["t"] = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)) },
                new() { ["a"] = "+cmd|' /C calc'!A0", ["n"] = -3, ["t"] = null },
                new() { ["a"] = "@SUM(1,1)", ["n"] = 0, ["t"] = null },
                new() { ["a"] = "-2+3", ["n"] = 7, ["t"] = null },
                new() { ["a"] = "plain, with \"quotes\"\nand newline", ["n"] = 2.25, ["t"] = null },
                new() { ["a"] = "-5", ["n"] = null, ["t"] = null },
            ]);

        var lines = ReportCsv.Write(report).Replace("\r\n", "\n");
        Assert.StartsWith("Name,Number,Time\n", lines);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\",1.5,2026-01-02T01:04:05Z", lines);
        Assert.Contains("'+cmd|' /C calc'!A0,-3,", lines);
        Assert.Contains("\"'@SUM(1,1)\",0,", lines);
        Assert.Contains("'-2+3,7,", lines);
        Assert.Contains("\"plain, with \"\"quotes\"\"\nand newline\",2.25,", lines);
        Assert.Contains("\n-5,,", lines);   // a plain negative number is not treated as a formula
    }
}

public sealed class TimeRangeTests
{
    [Fact]
    public void Day_range_uses_the_given_offset()
    {
        var range = TimeRange.ForDay(new DateOnly(2026, 3, 10), 330);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 18, 30, 0, TimeSpan.Zero), range.From);
        Assert.Equal(range.From.AddDays(1), range.To);
    }

    [Fact]
    public void Default_range_is_today_in_the_server_time_zone()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 3, 10, 20, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(5.5), "test", "test"));
        var range = TimeRange.Resolve(null, null, clock);
        Assert.Equal(new DateTimeOffset(2026, 3, 10, 18, 30, 0, TimeSpan.Zero), range.From);   // local 2026-03-11 00:00 (it is already the 11th in UTC+5:30)
        Assert.Equal(range.From.AddDays(1), range.To);
    }

    [Fact]
    public void Explicit_bounds_are_kept_and_an_inverted_range_is_repaired()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var from = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(2));
        var range = TimeRange.Resolve(from, from.AddHours(3), clock);
        Assert.Equal(from.ToUniversalTime(), range.From);
        Assert.Equal(from.AddHours(3).ToUniversalTime(), range.To);
        Assert.True(TimeRange.Resolve(from, from.AddHours(-3), clock).To > from);
    }
}

public sealed class RetentionWorkerScheduleTests
{
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test");

    [Fact]
    public void Runs_later_today_when_the_hour_has_not_come_yet()
    {
        var now = new DateTimeOffset(2026, 3, 10, 0, 30, 0, TimeSpan.Zero);   // 02:30 local
        Assert.Equal(TimeSpan.FromMinutes(30), RetentionWorker.DelayUntilNextRun(now, Plus2, 3));
    }

    [Fact]
    public void Runs_tomorrow_when_the_hour_has_passed()
    {
        var now = new DateTimeOffset(2026, 3, 10, 1, 30, 0, TimeSpan.Zero);   // 03:30 local
        Assert.Equal(TimeSpan.FromHours(23.5), RetentionWorker.DelayUntilNextRun(now, Plus2, 3));
    }
}

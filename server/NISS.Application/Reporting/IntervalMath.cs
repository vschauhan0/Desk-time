namespace NISS.Application.Reporting;

/// <summary>A half-open UTC interval [Start, End).</summary>
public readonly record struct Interval(DateTimeOffset Start, DateTimeOffset End)
{
    public double Seconds => End > Start ? (End - Start).TotalSeconds : 0;
    public bool IsEmpty => End <= Start;
}

/// <summary>Interval arithmetic used for active/idle time. All list arguments and results are sorted, merged and non-overlapping.</summary>
public static class IntervalMath
{
    /// <summary>Sorts, drops empty intervals and merges overlapping or touching ones.</summary>
    public static List<Interval> Normalize(IEnumerable<Interval> intervals)
    {
        var sorted = intervals.Where(i => !i.IsEmpty).OrderBy(i => i.Start).ToList();
        var result = new List<Interval>(sorted.Count);
        foreach (var i in sorted)
        {
            if (result.Count > 0 && i.Start <= result[^1].End)
            {
                if (i.End > result[^1].End) result[^1] = result[^1] with { End = i.End };
            }
            else result.Add(i);
        }
        return result;
    }

    /// <summary>Restricts every interval to <paramref name="window"/>.</summary>
    public static List<Interval> Clip(IEnumerable<Interval> intervals, Interval window) =>
        Normalize(intervals.Select(i => new Interval(i.Start > window.Start ? i.Start : window.Start, i.End < window.End ? i.End : window.End)));

    /// <summary>Intersection of two normalized lists.</summary>
    public static List<Interval> Intersect(IReadOnlyList<Interval> a, IReadOnlyList<Interval> b)
    {
        var result = new List<Interval>();
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            var start = a[x].Start > b[y].Start ? a[x].Start : b[y].Start;
            var end = a[x].End < b[y].End ? a[x].End : b[y].End;
            if (end > start) result.Add(new Interval(start, end));
            if (a[x].End < b[y].End) x++; else y++;
        }
        return result;
    }

    /// <summary>The parts of <paramref name="a"/> not covered by <paramref name="b"/> (both normalized).</summary>
    public static List<Interval> Subtract(IReadOnlyList<Interval> a, IReadOnlyList<Interval> b)
    {
        var result = new List<Interval>();
        var j = 0;
        foreach (var item in a)
        {
            var cursor = item.Start;
            while (j < b.Count && b[j].End <= cursor) j++;
            var k = j;
            while (k < b.Count && b[k].Start < item.End)
            {
                if (b[k].Start > cursor) result.Add(new Interval(cursor, b[k].Start));
                if (b[k].End > cursor) cursor = b[k].End;
                k++;
            }
            if (cursor < item.End) result.Add(new Interval(cursor, item.End));
        }
        return result;
    }

    public static double TotalSeconds(IEnumerable<Interval> intervals) => intervals.Sum(i => i.Seconds);
}

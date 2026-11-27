using NISS.Monitor.Protocol;

namespace NISS.Monitor.Core.Engines;

/// <summary>A flagged term that matched the foreground window.</summary>
/// <param name="Term">The configured term, as the administrator typed it.</param>
/// <param name="MatchedOn">One of <see cref="FlagMatchKinds"/>: the window title or the process name.</param>
public sealed record FlaggedMatch(string Term, string MatchedOn);

/// <summary>
/// Decides whether the current foreground window counts as flagged activity. Pure decision logic: it holds only the
/// per-term cooldown clock, performs no capture and touches no I/O, so the whole rule set is unit-testable.
/// </summary>
/// <remarks>
/// Matching is a case-insensitive substring test against the foreground window title first (this is how a website is
/// recognised - browsers put the page title in the window title) and the process name second. A term fires at most
/// once per cooldown window, and only on a <em>transition</em>: staying on a flagged window does not keep firing.
/// </remarks>
public sealed class FlaggedActivityDetector(TimeProvider time)
{
    private readonly Dictionary<string, DateTimeOffset> _lastFired = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSignature = "";

    /// <summary>
    /// Evaluates one foreground sample. Returns the match that should trigger a capture, or null when nothing
    /// matched, the policy is off, the same window already fired, or the term is still in cooldown.
    /// </summary>
    public FlaggedMatch? Evaluate(FlaggedActivityPolicy policy, string? processName, string? windowTitle)
    {
        if (policy is not { Enabled: true } || policy.Terms is null || policy.Terms.Count == 0) return null;

        // One capture per window, not per poll: the foreground is sampled about once a second.
        var signature = (processName ?? "") + "\u0000" + (windowTitle ?? "");
        if (signature == _lastSignature) return null;
        _lastSignature = signature;

        var match = FirstMatch(policy.Terms, processName, windowTitle);
        if (match is null) return null;

        var now = time.GetUtcNow();
        var cooldown = TimeSpan.FromSeconds(Math.Clamp(policy.CooldownSeconds, 0, 86_400));
        if (_lastFired.TryGetValue(match.Term, out var previous) && now - previous < cooldown) return null;

        _lastFired[match.Term] = now;
        return match;
    }

    /// <summary>Pure match test with no cooldown or transition state; exposed for configuration previews and tests.</summary>
    public static FlaggedMatch? FirstMatch(IReadOnlyList<string> terms, string? processName, string? windowTitle)
    {
        foreach (var raw in terms)
        {
            var term = raw?.Trim();
            if (string.IsNullOrEmpty(term)) continue;
            foreach (var candidate in Candidates(term))
            {
                if (Contains(windowTitle, candidate)) return new FlaggedMatch(term, FlagMatchKinds.Title);
                if (Contains(processName, candidate)) return new FlaggedMatch(term, FlagMatchKinds.Process);
            }
        }
        return null;
    }

    /// <summary>
    /// The strings a configured term is actually matched against: the term as typed, plus - when it looks like a URL
    /// or domain - the site's bare name.
    /// </summary>
    /// <remarks>
    /// Administrators naturally type a domain (<c>youtube.com</c>, <c>https://www.facebook.com/</c>), but a browser
    /// window title carries the <em>site name</em>, not the address: YouTube's title ends
    /// "<c>- YouTube - Brave</c>", which contains no ".com". Matching only the literal term would silently never
    /// fire. So a domain also matches its registrable label (<c>youtube.com</c> -&gt; <c>youtube</c>), which is the
    /// word browsers actually put in the title.
    /// </remarks>
    public static IReadOnlyList<string> Candidates(string term)
    {
        var trimmed = term.Trim();
        if (trimmed.Length == 0) return [];

        var host = HostOf(trimmed);
        if (host is null) return [trimmed];

        // Match the host, not the term as typed: no window title ever contains "https://www.youtube.com/feed".
        var label = RegistrableLabel(host);
        return label is null || label.Equals(host, StringComparison.OrdinalIgnoreCase)
            ? [host]
            : [host, label];
    }

    /// <summary>The host part of a URL or bare domain, or null when the term is not address-like.</summary>
    private static string? HostOf(string term)
    {
        var s = term;
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        // Drop any path, query or fragment, then any port and credentials.
        var cut = s.IndexOfAny(['/', '?', '#']);
        if (cut >= 0) s = s[..cut];
        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];
        var colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s[4..];

        // Address-like means "has a dot with label characters either side" - excludes plain words and "steam.exe".
        if (!s.Contains('.')) return null;
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return s.All(c => char.IsLetterOrDigit(c) || c is '.' or '-') && s.Length > 1 ? s : null;
    }

    /// <summary>
    /// Two-part public suffixes common enough to matter, so <c>bbc.co.uk</c> yields "bbc" rather than "co".
    /// Not a full public-suffix list: an unlisted multi-part suffix just yields a slightly broader term.
    /// </summary>
    private static readonly string[] TwoPartSuffixes =
        ["co.uk", "co.in", "co.jp", "co.nz", "co.za", "com.au", "com.br", "com.cn", "com.mx", "com.tr", "org.uk", "net.au", "ac.uk", "gov.uk"];

    /// <summary>The site's own name within a host: <c>www.youtube.com</c> -&gt; "youtube", <c>bbc.co.uk</c> -&gt; "bbc".</summary>
    private static string? RegistrableLabel(string host)
    {
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2) return null;
        var lastTwo = string.Join('.', labels[^2], labels[^1]);
        var index = TwoPartSuffixes.Contains(lastTwo, StringComparer.OrdinalIgnoreCase) ? labels.Length - 3 : labels.Length - 2;
        return index >= 0 && labels[index].Length > 1 ? labels[index] : null;
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

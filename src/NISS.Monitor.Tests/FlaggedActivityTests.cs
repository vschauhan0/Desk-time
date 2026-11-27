using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Live;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Tests;

public class FlaggedActivityDetectorTests
{
    private static FlaggedActivityPolicy Policy(bool enabled = true, int cooldown = 120, params string[] terms) =>
        new(enabled, terms, cooldown, true, 10, 2);

    [Fact]
    public void Matches_a_site_in_the_browser_window_title()
    {
        var detector = new FlaggedActivityDetector(new FakeTimeProvider());

        var match = detector.Evaluate(Policy(terms: "facebook"), "chrome", "Facebook - Google Chrome");

        Assert.NotNull(match);
        Assert.Equal("facebook", match.Term);
        Assert.Equal(FlagMatchKinds.Title, match.MatchedOn);
    }

    [Fact]
    public void Matches_a_process_name_when_the_title_does_not_contain_the_term()
    {
        var detector = new FlaggedActivityDetector(new FakeTimeProvider());

        var match = detector.Evaluate(Policy(terms: "steam.exe"), "steam.exe", "Store");

        Assert.NotNull(match);
        Assert.Equal(FlagMatchKinds.Process, match.MatchedOn);
    }

    [Theory]
    // The literal title recorded on the test machine when the youtube.com term failed to fire.
    [InlineData("youtube.com", "\"Maine Khud Ko Ragini MMS 2\" Song With Lyrics | Sunny Leone | Mustafa Zahid - YouTube - Brave")]
    [InlineData("https://www.youtube.com/", "Some Video - YouTube - Brave")]
    [InlineData("www.facebook.com", "Facebook - Google Chrome")]
    [InlineData("facebook.com/groups/x", "Facebook - Google Chrome")]
    [InlineData("bbc.co.uk", "Home - BBC News - Chrome")]
    public void A_domain_term_matches_the_site_name_a_browser_puts_in_the_title(string term, string title)
    {
        // Admins type a domain; the window title carries the site's name, never the address.
        var detector = new FlaggedActivityDetector(new FakeTimeProvider());

        var match = detector.Evaluate(Policy(terms: term), "brave", title);

        Assert.NotNull(match);
        Assert.Equal(term, match.Term);   // reported as the administrator typed it
        Assert.Equal(FlagMatchKinds.Title, match.MatchedOn);
    }

    [Theory]
    [InlineData("youtube.com", new[] { "youtube.com", "youtube" })]
    [InlineData("https://www.youtube.com/feed", new[] { "youtube.com", "youtube" })]
    [InlineData("bbc.co.uk", new[] { "bbc.co.uk", "bbc" })]
    [InlineData("mail.google.com", new[] { "mail.google.com", "google" })]
    [InlineData("youtube", new[] { "youtube" })]          // already a bare name
    [InlineData("steam.exe", new[] { "steam.exe" })]      // an executable, not a domain
    [InlineData("quarterly review", new[] { "quarterly review" })]
    public void Candidates_expands_only_address_like_terms(string term, string[] expected)
    {
        Assert.Equal(expected, FlaggedActivityDetector.Candidates(term));
    }

    [Fact]
    public void A_domain_term_does_not_match_an_unrelated_window()
    {
        var detector = new FlaggedActivityDetector(new FakeTimeProvider());

        Assert.Null(detector.Evaluate(Policy(terms: "youtube.com"), "excel", "Budget 2026.xlsx - Excel"));
    }

    [Fact]
    public void Does_not_match_when_disabled_or_when_no_terms_are_configured()
    {
        var detector = new FlaggedActivityDetector(new FakeTimeProvider());

        Assert.Null(detector.Evaluate(Policy(enabled: false, terms: "facebook"), "chrome", "Facebook - Google Chrome"));
        Assert.Null(new FlaggedActivityDetector(new FakeTimeProvider()).Evaluate(Policy(), "chrome", "Facebook - Google Chrome"));
    }

    [Fact]
    public void Does_not_match_an_unrelated_window()
    {
        var detector = new FlaggedActivityDetector(new FakeTimeProvider());

        Assert.Null(detector.Evaluate(Policy(terms: "facebook"), "excel", "Budget 2026.xlsx - Excel"));
    }

    [Fact]
    public void Fires_once_per_window_rather_than_on_every_poll()
    {
        var detector = new FlaggedActivityDetector(new FakeTimeProvider());
        var policy = Policy(terms: "facebook");

        Assert.NotNull(detector.Evaluate(policy, "chrome", "Facebook - Google Chrome"));
        // The foreground is sampled about once a second; the same window must not keep triggering captures.
        Assert.Null(detector.Evaluate(policy, "chrome", "Facebook - Google Chrome"));
        Assert.Null(detector.Evaluate(policy, "chrome", "Facebook - Google Chrome"));
    }

    [Fact]
    public void Holds_a_term_in_cooldown_then_fires_again_once_it_elapses()
    {
        var time = new FakeTimeProvider();
        var detector = new FlaggedActivityDetector(time);
        var policy = Policy(cooldown: 120, terms: "facebook");

        Assert.NotNull(detector.Evaluate(policy, "chrome", "Facebook - Google Chrome"));

        // Switching away and back inside the cooldown must not produce a second capture.
        Assert.Null(detector.Evaluate(policy, "excel", "Budget.xlsx - Excel"));
        time.Advance(TimeSpan.FromSeconds(60));
        Assert.Null(detector.Evaluate(policy, "chrome", "Facebook - Google Chrome"));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(detector.Evaluate(policy, "excel", "Budget.xlsx - Excel"));
        Assert.NotNull(detector.Evaluate(policy, "chrome", "Facebook - Google Chrome"));
    }

    [Fact]
    public void Cooldown_is_tracked_per_term_so_one_site_does_not_mask_another()
    {
        var detector = new FlaggedActivityDetector(new FakeTimeProvider());
        var policy = Policy(true, 120, "facebook", "youtube");

        Assert.Equal("facebook", detector.Evaluate(policy, "chrome", "Facebook - Chrome")?.Term);
        Assert.Equal("youtube", detector.Evaluate(policy, "chrome", "YouTube - Chrome")?.Term);
    }

    [Fact]
    public void Blank_and_whitespace_terms_are_ignored_rather_than_matching_everything()
    {
        var detector = new FlaggedActivityDetector(new FakeTimeProvider());

        // An empty term would otherwise be a substring of every title and flag the entire session.
        Assert.Null(detector.Evaluate(Policy(true, 120, "", "   "), "excel", "Budget.xlsx"));
    }

    [Fact]
    public void A_null_title_does_not_match_but_the_process_still_can()
    {
        var detector = new FlaggedActivityDetector(new FakeTimeProvider());

        // Window titles are null when the CaptureWindowTitles privacy gate is off.
        Assert.Null(detector.Evaluate(Policy(terms: "facebook"), "chrome", null));
        Assert.NotNull(new FlaggedActivityDetector(new FakeTimeProvider())
            .Evaluate(Policy(terms: "chrome"), "chrome", null));
    }
}

public class FlaggedCaptureTests
{
    private sealed class FakeCapture : IScreenCaptureService
    {
        public int Calls;
        public bool IsEnabled => true;
        public Task<ScreenCaptureResult> CaptureAsync(ScreenCaptureMode mode, string reason, CancellationToken ct) => Task.FromResult(ScreenCaptureResult.Disabled);
        public IReadOnlyList<MonitorInfo> EnumerateMonitors() => [new MonitorInfo(0, 0, 0, 100, 100, true)];
        public Task<ScreenCaptureFrame?> CaptureMonitorAsync(int monitorIndex, int maxWidth, int quality, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<ScreenCaptureFrame?>(new ScreenCaptureFrame([1, 2, 3], 100, 100, monitorIndex));
        }
    }

    private static ScreenCapturePolicy Policy(bool periodic, FlaggedActivityPolicy flagged) =>
        new(periodic, 60, 60, 1600, true, flagged);

    private static ScreenCaptureLoop Loop(FakeCapture capture, ScreenCapturePolicy policy, PresenceSnapshot presence,
        List<ScreenFrameHeader> sink, FakeTimeProvider time) =>
        new(capture, () => policy, () => presence, (h, _, _) => { sink.Add(h); return Task.CompletedTask; }, time);

    private static readonly FlaggedMatch Match = new("facebook", FlagMatchKinds.Title);

    [Fact]
    public async Task Flagged_capture_takes_a_still_and_a_clip_and_tags_every_frame()
    {
        var capture = new FakeCapture();
        var sink = new List<ScreenFrameHeader>();
        var time = new FakeTimeProvider();
        var flagged = new FlaggedActivityPolicy(true, ["facebook"], 120, CaptureClip: true, ClipSeconds: 2, ClipFps: 2);
        var loop = Loop(capture, Policy(periodic: false, flagged), new PresenceSnapshot(false, false), sink, time);

        var task = loop.CaptureFlaggedAsync(Match, default);
        // The clip paces itself off the injected clock, so advance it past every inter-frame delay.
        for (var i = 0; i < 10; i++) { time.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }
        var emitted = await task;

        // 1 still + (2 s x 2 fps) = 5 frames.
        Assert.Equal(5, emitted);
        Assert.Equal(5, sink.Count);
        Assert.All(sink, h => Assert.Equal(ScreenTriggers.Flagged, h.Trigger));
        Assert.All(sink, h => Assert.Equal("facebook", h.FlagTerm));
        Assert.All(sink, h => Assert.Equal(FlagMatchKinds.Title, h.MatchedOn));
    }

    [Fact]
    public async Task The_lead_still_has_no_clip_id_and_clip_frames_are_numbered_in_order()
    {
        var capture = new FakeCapture();
        var sink = new List<ScreenFrameHeader>();
        var time = new FakeTimeProvider();
        var flagged = new FlaggedActivityPolicy(true, ["facebook"], 120, CaptureClip: true, ClipSeconds: 2, ClipFps: 2);
        var loop = Loop(capture, Policy(periodic: false, flagged), new PresenceSnapshot(false, false), sink, time);

        var task = loop.CaptureFlaggedAsync(Match, default);
        for (var i = 0; i < 10; i++) { time.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }
        await task;

        Assert.Null(sink[0].ClipId);
        var clipFrames = sink.Skip(1).ToList();
        var clipId = clipFrames[0].ClipId;
        Assert.NotNull(clipId);
        Assert.All(clipFrames, f => Assert.Equal(clipId, f.ClipId));
        Assert.Equal([0, 1, 2, 3], clipFrames.Select(f => f.ClipSeq));
        Assert.All(clipFrames, f => Assert.Equal(4, f.ClipTotal));
    }

    [Fact]
    public async Task Flagged_capture_takes_only_a_still_when_clips_are_off()
    {
        var capture = new FakeCapture();
        var sink = new List<ScreenFrameHeader>();
        var flagged = new FlaggedActivityPolicy(true, ["facebook"], 120, CaptureClip: false, ClipSeconds: 10, ClipFps: 2);
        var loop = Loop(capture, Policy(periodic: false, flagged), new PresenceSnapshot(false, false), sink, new FakeTimeProvider());

        var emitted = await loop.CaptureFlaggedAsync(Match, default);

        Assert.Equal(1, emitted);
        Assert.Single(sink);
        Assert.Null(sink[0].ClipId);
    }

    [Fact]
    public async Task Flagged_capture_works_while_periodic_screenshots_are_switched_off()
    {
        var capture = new FakeCapture();
        var sink = new List<ScreenFrameHeader>();
        var flagged = new FlaggedActivityPolicy(true, ["facebook"], 120, false, 10, 2);
        var loop = Loop(capture, Policy(periodic: false, flagged), new PresenceSnapshot(false, false), sink, new FakeTimeProvider());

        Assert.False(await loop.TickAsync(default));            // periodic is off
        Assert.Equal(1, await loop.CaptureFlaggedAsync(Match, default)); // flagged still records
    }

    [Fact]
    public async Task Flagged_capture_is_refused_when_the_module_is_disabled()
    {
        var capture = new FakeCapture();
        var sink = new List<ScreenFrameHeader>();
        var flagged = new FlaggedActivityPolicy(Enabled: false, ["facebook"], 120, true, 10, 2);
        var loop = Loop(capture, Policy(periodic: true, flagged), new PresenceSnapshot(false, false), sink, new FakeTimeProvider());

        Assert.Equal(0, await loop.CaptureFlaggedAsync(Match, default));
        Assert.Empty(sink);
    }

    [Theory]
    [InlineData(true, false)]  // locked
    [InlineData(false, true)]  // idle
    public async Task Flagged_capture_honours_the_lock_and_idle_gate(bool locked, bool idle)
    {
        var capture = new FakeCapture();
        var sink = new List<ScreenFrameHeader>();
        var flagged = new FlaggedActivityPolicy(true, ["facebook"], 120, true, 10, 2);
        var loop = Loop(capture, Policy(periodic: true, flagged), new PresenceSnapshot(locked, idle), sink, new FakeTimeProvider());

        Assert.Equal(0, await loop.CaptureFlaggedAsync(Match, default));
        Assert.Equal(0, capture.Calls);
    }

    [Fact]
    public void Clip_frame_count_is_clamped_so_a_mistyped_policy_cannot_emit_thousands_of_frames()
    {
        Assert.Equal(20, new FlaggedActivityPolicy(true, [], 0, true, 10, 2).ClipFrameCount);
        Assert.Equal(1, new FlaggedActivityPolicy(true, [], 0, true, 0, 0).ClipFrameCount);
        Assert.Equal(FlaggedActivityPolicy.MaxClipFrames, new FlaggedActivityPolicy(true, [], 0, true, 600, 60).ClipFrameCount);
    }

    [Fact]
    public void A_policy_stored_before_this_feature_existed_reads_as_flagged_capture_off()
    {
        // FlaggedActivity is null when deserialised from an older policy row; it must not be treated as enabled.
        var policy = new ScreenCapturePolicy(true, 60, 60, 1600, true);

        Assert.False(policy.Flagged.Enabled);
        Assert.Empty(policy.Flagged.Terms);
    }
}

/// <summary>
/// The policy travels as JSON (stored in policy_json, pushed over the control pipe). Computed helpers must not
/// appear in it: they were being persisted alongside the real values, where they look authoritative but are ignored
/// on read.
/// </summary>
public class ScreenPolicySerializationTests
{
    [Fact]
    public void Computed_helpers_are_not_serialised()
    {
        var policy = new ScreenCapturePolicy(true, 60, 60, 1600, true,
            new FlaggedActivityPolicy(true, ["youtube.com"], 120, true, 10, 2));

        var json = JsonSerializer.Serialize(policy, ProtocolJson.Options);

        Assert.DoesNotContain("\"flagged\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clipFrameCount", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("flaggedActivity", json);
    }

    [Fact]
    public void A_policy_survives_a_json_round_trip_with_its_flagged_terms()
    {
        var policy = new ScreenCapturePolicy(true, 300, 72, 1920, true,
            new FlaggedActivityPolicy(true, ["youtube.com", "facebook"], 90, true, 8, 3));

        var round = JsonSerializer.Deserialize<ScreenCapturePolicy>(
            JsonSerializer.Serialize(policy, ProtocolJson.Options), ProtocolJson.Options);

        // Compared field by field, not with Assert.Equal(policy, round): these are records whose Terms member is an
        // IReadOnlyList, and record equality compares that by reference, so two equal policies never compare equal.
        Assert.NotNull(round);
        Assert.True(round.Enabled);
        Assert.Equal(300, round.IntervalSeconds);
        Assert.Equal(72, round.Quality);
        Assert.Equal(1920, round.MaxWidth);
        Assert.True(round.Flagged.Enabled);
        Assert.Equal(["youtube.com", "facebook"], round.Flagged.Terms);
        Assert.Equal(90, round.Flagged.CooldownSeconds);
        Assert.Equal(24, round.Flagged.ClipFrameCount);
    }
}

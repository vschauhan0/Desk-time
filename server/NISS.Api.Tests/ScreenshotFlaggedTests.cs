using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;
using NISS.Application.Retention;
using NISS.Application.Screenshots;
using NISS.Application.Settings;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class ScreenshotFlaggedTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string Url(string frameId, string? trigger = null, string? flagTerm = null,
        string? matchedOn = null, Guid? clipId = null, int clipSeq = 0, int clipTotal = 0)
    {
        var url = $"/api/v1/devices/screenshots?frameId={frameId}&monitor=0&width=10&height=10" +
                  $"&capturedAtUtc={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"))}&sessionUser=alice";
        if (trigger is not null) url += $"&trigger={trigger}";
        if (flagTerm is not null) url += $"&flagTerm={Uri.EscapeDataString(flagTerm)}";
        if (matchedOn is not null) url += $"&matchedOn={matchedOn}";
        if (clipId is not null) url += $"&clipId={clipId:D}&clipSeq={clipSeq}&clipTotal={clipTotal}";
        return url;
    }

    private static ByteArrayContent Jpeg() => new([1, 2, 3, 4]);

    [Fact]
    public async Task Flagged_upload_stores_the_trigger_term_and_match_kind()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-FL1");

        var resp = await device.Client.PostAsync(
            Url("fl-1", ScreenTriggers.Flagged, "facebook", FlagMatchKinds.Title), Jpeg());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = await admin.GetJsonAsync<Paged<ScreenshotDto>>($"/api/v1/devices/{device.DeviceId}/screenshots");
        var shot = Assert.Single(page.Items, s => s.Trigger == ScreenTriggers.Flagged);
        Assert.Equal("facebook", shot.FlagTerm);
        Assert.Equal(FlagMatchKinds.Title, shot.MatchedOn);
    }

    [Fact]
    public async Task A_periodic_upload_defaults_to_the_periodic_trigger_and_carries_no_flag_fields()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-FL2");

        await device.Client.PostAsync(Url("fl-2"), Jpeg());

        var page = await admin.GetJsonAsync<Paged<ScreenshotDto>>($"/api/v1/devices/{device.DeviceId}/screenshots");
        var shot = Assert.Single(page.Items);
        Assert.Equal(ScreenTriggers.Periodic, shot.Trigger);
        Assert.Null(shot.FlagTerm);
        Assert.Null(shot.ClipId);
    }

    [Fact]
    public async Task Flag_fields_on_a_periodic_upload_are_discarded_rather_than_trusted()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-FL3");

        // A periodic frame claiming a flag term must not be stored as evidence of flagged activity.
        await device.Client.PostAsync(Url("fl-3", ScreenTriggers.Periodic, "facebook", FlagMatchKinds.Title, Guid.NewGuid(), 2, 9), Jpeg());

        var page = await admin.GetJsonAsync<Paged<ScreenshotDto>>($"/api/v1/devices/{device.DeviceId}/screenshots");
        var shot = Assert.Single(page.Items);
        Assert.Null(shot.FlagTerm);
        Assert.Null(shot.MatchedOn);
        Assert.Null(shot.ClipId);
        Assert.Equal(0, shot.ClipSeq);
    }

    [Fact]
    public async Task An_unknown_trigger_is_normalised_to_periodic()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-FL4");

        await device.Client.PostAsync(Url("fl-4", "NONSENSE"), Jpeg());

        var page = await admin.GetJsonAsync<Paged<ScreenshotDto>>($"/api/v1/devices/{device.DeviceId}/screenshots");
        Assert.Equal(ScreenTriggers.Periodic, Assert.Single(page.Items).Trigger);
    }

    [Fact]
    public async Task The_timeline_can_be_filtered_to_flagged_or_periodic_captures()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-FL5");
        await device.Client.PostAsync(Url("fl-5a"), Jpeg());
        await device.Client.PostAsync(Url("fl-5b", ScreenTriggers.Flagged, "youtube", FlagMatchKinds.Title), Jpeg());

        var flagged = await admin.GetJsonAsync<Paged<ScreenshotDto>>($"/api/v1/devices/{device.DeviceId}/screenshots?trigger=FLAGGED");
        var periodic = await admin.GetJsonAsync<Paged<ScreenshotDto>>($"/api/v1/devices/{device.DeviceId}/screenshots?trigger=PERIODIC");

        Assert.Equal("youtube", Assert.Single(flagged.Items).FlagTerm);
        Assert.Equal(ScreenTriggers.Periodic, Assert.Single(periodic.Items).Trigger);
    }

    [Fact]
    public async Task An_invalid_trigger_filter_is_rejected()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-FL6");

        var resp = await admin.GetAsync($"/api/v1/devices/{device.DeviceId}/screenshots?trigger=sideways");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_clip_collapses_to_one_timeline_entry_but_every_frame_is_retrievable()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-FL7");
        var clip = Guid.NewGuid();
        for (var seq = 0; seq < 3; seq++)
            await device.Client.PostAsync(Url($"fl-7-{seq}", ScreenTriggers.Flagged, "betting", FlagMatchKinds.Title, clip, seq, 3), Jpeg());

        var collapsed = await admin.GetJsonAsync<Paged<ScreenshotDto>>($"/api/v1/devices/{device.DeviceId}/screenshots");
        var expanded = await admin.GetJsonAsync<Paged<ScreenshotDto>>($"/api/v1/devices/{device.DeviceId}/screenshots?includeClipFrames=true");

        // One tile per clip by default, so a burst does not swamp the timeline.
        Assert.Equal(0, Assert.Single(collapsed.Items).ClipSeq);
        Assert.Equal(3, expanded.Items.Count);
    }

    [Fact]
    public async Task The_clip_endpoint_returns_frames_in_order_and_audits_the_view()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-FL8");
        var clip = Guid.NewGuid();
        // Uploaded out of order on purpose: playback order comes from the sequence, not arrival.
        foreach (var seq in new[] { 2, 0, 1 })
            await device.Client.PostAsync(Url($"fl-8-{seq}", ScreenTriggers.Flagged, "facebook", FlagMatchKinds.Title, clip, seq, 3), Jpeg());

        var dto = await admin.GetJsonAsync<ScreenshotClipDto>($"/api/v1/devices/{device.DeviceId}/screenshots/clips/{clip:D}");

        Assert.Equal(clip, dto.ClipId);
        Assert.Equal("facebook", dto.FlagTerm);
        Assert.Equal(3, dto.FrameCount);
        Assert.Equal(3, dto.ExpectedFrameCount);
        Assert.Equal([0, 1, 2], dto.Frames.Select(f => f.ClipSeq));

        var logs = await admin.GetJsonAsync<Paged<AuditLogDto>>("/api/v1/audit-logs?pageSize=500&action=SCREENSHOT_VIEWED");
        Assert.Contains(logs.Items, l => l.Target == device.DeviceId && l.Detail != null && l.Detail.Contains("clip"));
    }

    [Fact]
    public async Task An_incomplete_clip_reports_fewer_frames_than_expected()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-FL9");
        var clip = Guid.NewGuid();
        // The session locked mid-clip: only 2 of the promised 8 frames arrived.
        for (var seq = 0; seq < 2; seq++)
            await device.Client.PostAsync(Url($"fl-9-{seq}", ScreenTriggers.Flagged, "facebook", FlagMatchKinds.Title, clip, seq, 8), Jpeg());

        var dto = await admin.GetJsonAsync<ScreenshotClipDto>($"/api/v1/devices/{device.DeviceId}/screenshots/clips/{clip:D}");

        Assert.Equal(2, dto.FrameCount);
        Assert.Equal(8, dto.ExpectedFrameCount);
    }

    [Fact]
    public async Task An_unknown_clip_is_not_found()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-FL10");

        var resp = await admin.GetAsync($"/api/v1/devices/{device.DeviceId}/screenshots/clips/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Clips_are_scoped_to_their_device()
    {
        var admin = await factory.AdminAsync();
        var owner = await factory.EnrollDeviceAsync(admin, "EMP-FL11");
        var other = await factory.EnrollDeviceAsync(admin, "EMP-FL12");
        var clip = Guid.NewGuid();
        await owner.Client.PostAsync(Url("fl-11", ScreenTriggers.Flagged, "facebook", FlagMatchKinds.Title, clip, 0, 1), Jpeg());

        var resp = await admin.GetAsync($"/api/v1/devices/{other.DeviceId}/screenshots/clips/{clip:D}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Flagged_captures_survive_the_periodic_retention_cutoff_on_their_own_schedule()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-FL13");
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IScreenshotStore>();
        var retention = scope.ServiceProvider.GetRequiredService<IRetentionService>();
        var d = await db.Devices.FirstAsync(x => x.DeviceCode == device.DeviceId);
        var age = factory.Clock.GetUtcNow().AddDays(-20);
        // Tests share a database, so pin the two cutoffs instead of relying on whatever a previous test saved.
        await scope.ServiceProvider.GetRequiredService<ISettingsService>()
            .SetRetentionAsync(new RetentionSettings(90, 365, 180, 365, ScreenshotDays: 14, FlaggedScreenshotDays: 90), default);

        // 20 days old: past the 14-day periodic cutoff, inside the 90-day flagged one.
        var periodic = await store.SaveAsync(
            new ScreenshotInput(d.Id, d.DeviceCode, d.EmployeeId, "alice", "fl-ret-p", age, 0, 10, 10), [1, 2, 3], default);
        var flagged = await store.SaveAsync(
            new ScreenshotInput(d.Id, d.DeviceCode, d.EmployeeId, "alice", "fl-ret-f", age, 0, 10, 10,
                ScreenTriggers.Flagged, "facebook", FlagMatchKinds.Title), [1, 2, 3], default);

        await retention.RunAsync(default);

        Assert.False(await db.Screenshots.AnyAsync(s => s.Id == periodic.Id));
        Assert.True(await db.Screenshots.AnyAsync(s => s.Id == flagged.Id));
        Assert.True(File.Exists(store.FullPath(flagged)));
    }

    [Fact]
    public async Task Two_captures_in_the_same_tick_on_the_same_monitor_get_separate_files()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-FL15");
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IScreenshotStore>();
        var d = await db.Devices.FirstAsync(x => x.DeviceCode == device.DeviceId);
        var sameInstant = factory.Clock.GetUtcNow();

        // A flagged still can be taken in the same tick as a periodic one. If they shared a path, the second write
        // would overwrite the first and deleting either row would destroy the surviving row's image.
        var first = await store.SaveAsync(
            new ScreenshotInput(d.Id, d.DeviceCode, d.EmployeeId, "alice", "fl-15-a", sameInstant, 0, 10, 10), [1], default);
        var second = await store.SaveAsync(
            new ScreenshotInput(d.Id, d.DeviceCode, d.EmployeeId, "alice", "fl-15-b", sameInstant, 0, 10, 10,
                ScreenTriggers.Flagged, "facebook", FlagMatchKinds.Title), [2, 2], default);

        Assert.NotEqual(first.RelPath, second.RelPath);
        Assert.True(File.Exists(store.FullPath(first)));
        Assert.True(File.Exists(store.FullPath(second)));

        // Deleting one must leave the other's bytes intact.
        await store.DeleteAsync([first], default);
        Assert.False(File.Exists(store.FullPath(first)));
        Assert.True(File.Exists(store.FullPath(second)));
        Assert.Equal(2, (await File.ReadAllBytesAsync(store.FullPath(second))).Length);
    }

    [Fact]
    public async Task Flagged_captures_are_deleted_once_their_own_cutoff_passes()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-FL14");
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IScreenshotStore>();
        var retention = scope.ServiceProvider.GetRequiredService<IRetentionService>();
        var d = await db.Devices.FirstAsync(x => x.DeviceCode == device.DeviceId);
        await scope.ServiceProvider.GetRequiredService<ISettingsService>()
            .SetRetentionAsync(new RetentionSettings(90, 365, 180, 365, ScreenshotDays: 14, FlaggedScreenshotDays: 90), default);

        var ancient = await store.SaveAsync(
            new ScreenshotInput(d.Id, d.DeviceCode, d.EmployeeId, "alice", "fl-ret-old",
                factory.Clock.GetUtcNow().AddDays(-200), 0, 10, 10,
                ScreenTriggers.Flagged, "facebook", FlagMatchKinds.Title), [1, 2, 3], default);
        var path = store.FullPath(ancient);

        await retention.RunAsync(default);

        Assert.False(File.Exists(path));
        Assert.False(await db.Screenshots.AnyAsync(s => s.Id == ancient.Id));
    }
}

/// <summary>
/// A policy row written before screen capture / flagged activity existed deserialises those members as null.
/// The API must never hand that null to a client: the dashboard dereferences the object to render the editor.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LegacyPolicyNormalisationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>Overwrites the global policy_json and returns a handle that puts the original back.</summary>
    private async Task<IAsyncDisposable> OverwriteGlobalPolicyJsonAsync(string json)
    {
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var row = await db.ConfigurationPolicies.FirstAsync(p => p.Scope == NISS.Domain.PolicyScope.Global);
        var original = row.PolicyJson;
        row.PolicyJson = json;
        await db.SaveChangesAsync(default);
        return new Restore(factory, original);
    }

    private sealed class Restore(ApiFactory factory, string original) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            using var scope = factory.Scope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var row = await db.ConfigurationPolicies.FirstAsync(p => p.Scope == NISS.Domain.PolicyScope.Global);
            row.PolicyJson = original;
            await db.SaveChangesAsync(default);
        }
    }

    [Fact]
    public async Task A_policy_row_predating_the_feature_is_served_with_flagged_activity_filled_in()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-LEGACY");

        // Write policy_json exactly as it looked before this feature: no screenCapture member at all.
        await using var _ = await OverwriteGlobalPolicyJsonAsync("""{"heartbeatSeconds":30,"idleThresholdMinutes":5}""");

        var policies = await admin.GetJsonAsync<List<PolicyDto>>("/api/v1/settings/policies");
        var global = Assert.Single(policies, p => p.Scope == "GLOBAL");

        Assert.NotNull(global.Policy.ScreenCapture);
        Assert.NotNull(global.Policy.ScreenCapture.FlaggedActivity);
        // Missing means off, never on.
        Assert.False(global.Policy.ScreenCapture.Enabled);
        Assert.False(global.Policy.ScreenCapture.FlaggedActivity!.Enabled);
        Assert.Empty(global.Policy.ScreenCapture.FlaggedActivity.Terms);

        // The agent's effective policy is normalised the same way.
        var cfg = await device.Client.GetJsonAsync<AgentConfigResponse>("/api/v1/agent/config");
        Assert.NotNull(cfg.Policy.ScreenCapture.FlaggedActivity);
        Assert.False(cfg.Policy.ScreenCapture.FlaggedActivity!.Enabled);
    }

    [Fact]
    public async Task An_explicit_null_flagged_activity_is_also_filled_in()
    {
        var admin = await factory.AdminAsync();

        await using var _ = await OverwriteGlobalPolicyJsonAsync(
            """{"screenCapture":{"enabled":true,"intervalSeconds":300,"quality":60,"maxWidth":1600,"liveEnabled":true,"flaggedActivity":null}}""");

        var policies = await admin.GetJsonAsync<List<PolicyDto>>("/api/v1/settings/policies");
        var global = Assert.Single(policies, p => p.Scope == "GLOBAL");

        Assert.NotNull(global.Policy.ScreenCapture.FlaggedActivity);
        Assert.False(global.Policy.ScreenCapture.FlaggedActivity!.Enabled);
        Assert.True(global.Policy.ScreenCapture.Enabled);   // the rest of the stored policy survives
    }
}

/// <summary>The dashboard overview surfaces a fleet-wide flagged-activity summary (counts + site names, no images).</summary>
[Collection(ApiCollection.Name)]
public sealed class OverviewFlaggedSummaryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Overview_reports_flagged_captures_today()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-OVF");
        var url = $"/api/v1/devices/screenshots?frameId=ovf-1&monitor=0&width=10&height=10" +
                  $"&capturedAtUtc={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"))}&sessionUser=alice" +
                  $"&trigger={ScreenTriggers.Flagged}&flagTerm=youtube.com&matchedOn={FlagMatchKinds.Title}";
        await device.Client.PostAsync(url, new ByteArrayContent([1, 2, 3]));

        var overview = await admin.GetJsonAsync<OverviewDto>("/api/v1/overview");

        Assert.True(overview.FlaggedToday >= 1);
        Assert.NotNull(overview.RecentFlagged);
        Assert.Contains(overview.RecentFlagged!, f => f.FlagTerm == "youtube.com" && f.DeviceId == device.DeviceId);
    }
}

using System.Net;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Infrastructure.Http;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Infrastructure.Persistence;
using NISS.Monitor.Infrastructure.Runtime;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Tests;

public class EnrollmentTests
{
    [Fact]
    public async Task Enrollment_stores_identity_with_protected_secret_and_policy()
    {
        await using var host = new AgentTestHost();
        var id = await host.EnrollAsync("EMP1024");

        Assert.Equal("PC-024", id.DeviceId);
        Assert.Equal("EMP1024", id.EmployeeId);

        // The secret is not stored in clear text in agent.db (DPAPI machine scope).
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var raw = File.ReadAllBytes(host.Get<SqliteDatabase>().Path);
        Assert.False(System.Text.Encoding.UTF8.GetString(raw).Contains("s3cret-value", StringComparison.Ordinal));

        // ... and a fresh process (new store instance) can decrypt it again.
        Assert.Equal("s3cret-value", new DeviceIdentityStore(host.Get<SqliteDatabase>()).Reload()!.DeviceSecret);
        Assert.Equal(1, host.Get<IPolicyProvider>().Current.Version);
    }

    [Fact]
    public async Task Bad_enrollment_token_fails_without_creating_an_identity()
    {
        await using var host = new AgentTestHost();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Get<IDeviceEnrollmentService>().EnrollAsync(new EnrollmentRequestOptions("https://niss.test:8443", "WRONG", null, null), default));
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.Null(host.Get<DeviceIdentityStore>().Reload());
    }

    [Fact]
    public async Task Requests_carry_the_per_device_credential_and_never_a_shared_key()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        await host.Get<IHeartbeatService>().SendAsync(default);
        Assert.Equal("NissDevice PC-024:s3cret-value", host.Server.LastAuthorization);
    }
}

public class SqliteQueueTests
{
    [Fact]
    public async Task Events_come_back_in_sequence_order_and_duplicates_are_ignored()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var a = await host.QueueAsync(EventTypes.UserLogin);
        var b = await host.QueueAsync(EventTypes.SessionLock);
        await host.Get<IEventQueue>().EnqueueAsync(a, default); // same event id again

        var peek = await host.Get<IEventQueue>().PeekBatchAsync(10, default);
        Assert.Equal(new[] { a.EventId, b.EventId }, peek.Select(p => p.EventId));
        Assert.True(a.Sequence < b.Sequence);
    }

    [Fact]
    public async Task Queue_survives_a_service_restart()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var e = await host.QueueAsync(EventTypes.UserLogin);

        // "restart": brand-new queue + sequence generator on the same database file
        var queue2 = new SqliteEventQueue(new SqliteDatabase(host.Get<NISS.Monitor.Core.Configuration.AgentPaths>()), host.Time);
        Assert.Equal(e.EventId, (await queue2.PeekBatchAsync(5, default)).Single().EventId);
        Assert.Equal(e.Sequence, await queue2.GetMaxSequenceAsync(default));
    }

    [Fact]
    public async Task Dead_lettered_events_do_not_block_the_queue()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var q = host.Get<IEventQueue>();
        var bad = await host.QueueAsync(EventTypes.AgentError);
        var good = await host.QueueAsync(EventTypes.UserLogin);
        await q.MarkDeadLetterAsync([(bad.EventId, "invalid")], default);

        Assert.Equal(good.EventId, (await q.PeekBatchAsync(5, default)).Single().EventId);
        var stats = await q.GetStatsAsync(default);
        Assert.Equal((1, 1), (stats.Pending, stats.DeadLetters));
    }

    [Fact]
    public async Task Backing_off_head_event_blocks_later_events_from_overtaking_it()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var q = host.Get<IEventQueue>();
        var first = await host.QueueAsync(EventTypes.UserLogin);
        await host.QueueAsync(EventTypes.SessionLock);
        await q.MarkFailedAsync([first.EventId], "boom", TimeSpan.FromSeconds(30), default);

        Assert.Empty(await q.PeekBatchAsync(10, default));                  // strict ordering: nothing may pass the failed head
        host.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(2, (await q.PeekBatchAsync(10, default)).Count);       // back-off elapsed
    }

    [Fact]
    public async Task Pending_cap_drops_low_value_telemetry_first()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var keep = await host.QueueAsync(EventTypes.UserLogin);
        for (var i = 0; i < 5; i++) await host.QueueAsync(EventTypes.SystemMetric);
        await host.Get<IEventQueue>().MaintainAsync(TimeSpan.FromHours(1), maxPending: 3, default);

        var left = await host.Get<IEventQueue>().PeekBatchAsync(100, default);
        Assert.Equal(3, left.Count);
        Assert.Contains(left, l => l.EventId == keep.EventId);
    }
}

public class OfflineFirstUploadTests
{
    [Fact]
    public async Task Server_outage_keeps_events_locally_and_backlog_uploads_in_order_after_reconnect()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var uploader = host.Get<IUploader>();

        host.Server.Online = false;
        var sent = new List<EventEnvelope>();
        for (var i = 0; i < 5; i++) { sent.Add(await host.QueueAsync(EventTypes.SystemMetric)); host.Time.Advance(TimeSpan.FromSeconds(1)); }

        var offline = await uploader.UploadPendingAsync(default);
        Assert.False(offline.ServerReachable);
        Assert.Equal(5, (await host.Get<IEventQueue>().GetStatsAsync(default)).Pending);   // nothing lost

        // Employee keeps working while offline: more events arrive.
        sent.Add(await host.QueueAsync(EventTypes.ApplicationForeground));

        host.Server.Online = true;
        host.Time.Advance(TimeSpan.FromMinutes(6)); // past the exponential back-off
        var online = await uploader.UploadPendingAsync(default);

        Assert.True(online.ServerReachable);
        Assert.Equal(6, online.Uploaded);
        Assert.Equal(sent.Select(s => s.EventId), host.Server.ReceivedOrder);               // chronological / sequence order preserved
        Assert.Equal(0, (await host.Get<IEventQueue>().GetStatsAsync(default)).Pending);
    }

    [Fact]
    public async Task Retry_backs_off_exponentially_and_does_not_hammer_the_server()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        await host.QueueAsync(EventTypes.UserLogin);
        host.Server.Online = false;
        var uploader = host.Get<IUploader>();

        await uploader.UploadPendingAsync(default);
        var stats = await host.Get<IEventQueue>().GetStatsAsync(default);
        Assert.Equal(1, stats.Pending);
        Assert.Empty(await host.Get<IEventQueue>().PeekBatchAsync(5, default));            // still backing off right now
        host.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Single(await host.Get<IEventQueue>().PeekBatchAsync(5, default));            // eligible again after the first delay
    }

    [Fact]
    public async Task Resending_an_already_stored_event_is_handled_as_duplicate_without_data_loss()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var e = await host.QueueAsync(EventTypes.UserLogin);
        host.Server.Stored.Add(e.EventId); // e.g. the response to an earlier upload was lost in transit

        var r = await host.Get<IUploader>().UploadPendingAsync(default);
        Assert.Equal((0, 1), (r.Uploaded, r.Duplicates));
        Assert.Equal(0, (await host.Get<IEventQueue>().GetStatsAsync(default)).Pending);
    }

    [Fact]
    public async Task Rejected_event_is_dead_lettered_and_the_rest_still_upload()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        host.Server.RejectTypes.Add(EventTypes.AgentError);
        await host.QueueAsync(EventTypes.AgentError);
        await host.QueueAsync(EventTypes.UserLogin);

        var r = await host.Get<IUploader>().UploadPendingAsync(default);
        Assert.Equal((1, 1), (r.Uploaded, r.DeadLettered));
        var stats = await host.Get<IEventQueue>().GetStatsAsync(default);
        Assert.Equal((0, 1), (stats.Pending, stats.DeadLetters));
    }

    [Fact]
    public async Task Poison_event_in_a_batch_is_isolated_instead_of_blocking_everything()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var good1 = await host.QueueAsync(EventTypes.UserLogin);
        var poison = await host.QueueAsync(EventTypes.AgentError);
        var good2 = await host.QueueAsync(EventTypes.SessionLock);
        host.Server.PoisonIds.Add(poison.EventId);

        var r = await host.Get<IUploader>().UploadPendingAsync(default);
        Assert.Equal(2, r.Uploaded);
        Assert.Equal(1, r.DeadLettered);
        Assert.Equal(new[] { good1.EventId, good2.EventId }, host.Server.ReceivedOrder);
    }

    [Fact]
    public async Task Authentication_failure_is_reported_and_events_are_kept()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        await host.QueueAsync(EventTypes.UserLogin);
        host.Server.ForceStatus = HttpStatusCode.Unauthorized;

        var r = await host.Get<IUploader>().UploadPendingAsync(default);
        Assert.True(r.AuthFailure);
        Assert.Equal(1, (await host.Get<IEventQueue>().GetStatsAsync(default)).Pending);
    }

    [Fact]
    public async Task Disabled_device_uploads_nothing()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        await host.QueueAsync(EventTypes.UserLogin);
        host.Get<AgentGate>().Suspend("disabled by administrator");
        var r = await host.Get<IUploader>().UploadPendingAsync(default);
        Assert.Equal(0, r.Uploaded);
        Assert.Empty(host.Server.ReceivedOrder);
    }
}

public class HeartbeatTests
{
    [Fact]
    public async Task Heartbeat_reports_state_and_measures_clock_offset_from_server_time()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        host.Server.ServerClockAhead = TimeSpan.FromSeconds(42); // the server's clock is 42 s ahead of this device
        await host.QueueAsync(EventTypes.UserLogin);

        Assert.True(await host.Get<IHeartbeatService>().SendAsync(default));

        var hb = host.Server.LastHeartbeat!;
        Assert.Equal("PC-024", hb.DeviceId);
        Assert.Equal(1, hb.QueueDepth);
        Assert.Equal("Running", hb.ServiceState);
        Assert.InRange(host.Get<NISS.Monitor.Core.Engines.ClockOffsetTracker>().OffsetMs, 41_000, 43_000);
    }

    [Fact]
    public async Task Events_are_stamped_with_offset_corrected_time()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        host.Server.ServerClockAhead = TimeSpan.FromSeconds(30);
        await host.Get<IHeartbeatService>().SendAsync(default);

        var raw = host.Time.GetUtcNow();
        var e = await host.QueueAsync(EventTypes.UserLogin, at: raw);
        Assert.InRange((e.TimestampUtc - raw).TotalSeconds, 29, 31);
        Assert.Equal(e.ClockOffsetMs, host.Get<NISS.Monitor.Core.Engines.ClockOffsetTracker>().OffsetMs);
    }

    [Fact]
    public async Task Heartbeat_failure_when_server_is_down_is_a_result_not_an_exception()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        host.Server.Online = false;
        Assert.False(await host.Get<IHeartbeatService>().SendAsync(default));
        Assert.Single(host.Get<LocalTelemetryStore>().RecentHeartbeats(5), h => !h.Success);
    }

    [Fact]
    public async Task Newer_config_version_makes_the_agent_fetch_and_apply_the_new_policy()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        host.Server.ConfigVersion = 5;
        await host.Get<IHeartbeatService>().SendAsync(default);
        Assert.Equal(5, host.Get<IPolicyProvider>().Current.Version);
        Assert.Equal(7, host.Get<IPolicyProvider>().Current.IdleThresholdMinutes);
    }

    [Fact]
    public async Task Server_disabling_the_device_suspends_collection_until_it_is_re_enabled()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var hb = host.Get<IHeartbeatService>();
        host.Server.DeviceStatus = "REVOKED";
        await hb.SendAsync(default);
        Assert.True(host.Get<AgentGate>().Suspended);
        host.Server.DeviceStatus = "ACTIVE";
        await hb.SendAsync(default);
        Assert.False(host.Get<AgentGate>().Suspended);
    }
}

using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Retention;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class RetentionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static Task<long> Count(string table, TestDevice d) => TestDatabase.ScalarAsync<long>(
        $"SELECT count(*) FROM {table} t JOIN devices dev ON dev.id = t.device_id WHERE dev.device_id = @d", ("d", d.DeviceId));

    private async Task<IReadOnlyDictionary<string, long>> RunRetentionAsync()
    {
        using var scope = factory.Scope();
        return await scope.ServiceProvider.GetRequiredService<IRetentionService>().RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Old_rows_are_pruned_per_retention_class_and_recent_rows_are_kept()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-RET");
        var now = factory.Clock.GetUtcNow();
        var old = now.AddDays(-200);      // beyond raw (90) and device health (180), within application summaries (365)
        var ancient = now.AddDays(-400);  // beyond everything
        var recent = now.AddDays(-2);

        async Task Send(DateTimeOffset at)
        {
            var app = Guid.NewGuid(); var proc = Guid.NewGuid();
            await d.IngestAsync(
                d.Event(EventTypes.UsbConnected, at, P.Usb()),
                d.Event(EventTypes.FileCreated, at, P.File()),
                d.Event(EventTypes.SystemMetric, at, P.Metric()),
                d.Event(EventTypes.KeyboardActivity, at, P.Keyboard(at, 10)),
                d.Event(EventTypes.IdleEnded, at, P.Idle(at.AddMinutes(-10), 600)),
                d.Event(EventTypes.ProcessStarted, at.AddMinutes(-3), P.Process(proc, "old.exe", at.AddMinutes(-3))),
                d.Event(EventTypes.ProcessStopped, at, P.Process(proc, "old.exe", at.AddMinutes(-3), at)),
                d.Event(EventTypes.ApplicationBackground, at, P.Background(app, at.AddMinutes(-5), at, 100)));
        }
        await Send(recent); await Send(old); await Send(ancient);

        Assert.Equal(24L, await Count("event_ingestion", d));

        var removed = await RunRetentionAsync();

        // Raw events (90 days): only the recent set remains.
        Assert.Equal(8L, await Count("event_ingestion", d));
        Assert.Equal(1L, await Count("usb_events", d));
        Assert.Equal(1L, await Count("file_events", d));
        Assert.Equal(1L, await Count("input_activity", d));
        Assert.Equal(1L, await Count("idle_sessions", d));
        Assert.Equal(2L, await Count("process_events", d));
        // Device health (180 days): the 200-day-old metric is gone too.
        Assert.Equal(1L, await Count("system_metrics", d));
        // Application/process summaries (365 days): the 200-day-old sessions survive, the 400-day-old ones do not.
        Assert.Equal(2L, await Count("application_sessions", d));
        Assert.Equal(2L, await Count("process_sessions", d));
        Assert.True(removed["rawEvents"] >= 16);
        Assert.True(removed["applicationSummaries"] >= 2);
    }

    [Fact]
    public async Task Zero_days_means_keep_forever_and_batches_delete_everything_that_is_due()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-RET2");
        var ancient = factory.Clock.GetUtcNow().AddDays(-1000);
        await d.IngestAsync(d.Event(EventTypes.UsbConnected, ancient, P.Usb()));

        await admin.PutJsonAsync("/api/v1/settings/retention", new RetentionSettings(0, 0, 0, 0));
        await RunRetentionAsync();
        Assert.Equal(1L, await Count("event_ingestion", d));
        Assert.True(await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM audit_logs") > 0);

        // Many due rows are removed in batches (more than one 5000-row batch would be needed at scale; here: all of them).
        await TestDatabase.ExecuteAsync(
            "INSERT INTO usb_events (id, device_id, event_type, device_type, \"timestamp\") SELECT gen_random_uuid(), d.id, 'USB_CONNECTED', 'X', now() - interval '500 days' FROM devices d, generate_series(1, 6000) WHERE d.device_id = @d",
            ("d", d.DeviceId));
        Assert.Equal(6001L, await Count("usb_events", d));
        await admin.PutJsonAsync("/api/v1/settings/retention", new RetentionSettings(90, 365, 180, 365));
        await RunRetentionAsync();
        Assert.Equal(0L, await Count("usb_events", d));
        Assert.Equal(0L, await Count("event_ingestion", d));
    }

    [Fact]
    public async Task Audit_logs_are_pruned_by_their_own_setting()
    {
        var admin = await factory.AdminAsync();
        await TestDatabase.ExecuteAsync(
            "INSERT INTO audit_logs (id, \"timestamp\", username, action) VALUES (gen_random_uuid(), now() - interval '500 days', 'ancient', 'OLD_ACTION'), (gen_random_uuid(), now() - interval '10 days', 'recent', 'NEW_ACTION')");
        Assert.Equal(2L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE action IN ('OLD_ACTION', 'NEW_ACTION')"));
        await admin.PutJsonAsync("/api/v1/settings/retention", new RetentionSettings(90, 365, 180, 365));
        await RunRetentionAsync();
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE action = 'NEW_ACTION'"));
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE action = 'OLD_ACTION'"));
    }

    [Fact]
    public async Task Stale_enrollment_tokens_are_cleaned_up()
    {
        var admin = await factory.AdminAsync();
        var fresh = await admin.CreateTokenAsync("EMP-CLEAN");
        var stale = await admin.CreateTokenAsync("EMP-CLEAN2");
        await TestDatabase.ExecuteAsync("UPDATE enrollment_tokens SET expires_at = now() - interval '30 days' WHERE id = @i", ("i", stale.Id));

        using (var scope = factory.Scope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<IEnrollmentTokenCleanupService>().RunAsync(CancellationToken.None) >= 1);

        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM enrollment_tokens WHERE id = @i", ("i", fresh.Id)));
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM enrollment_tokens WHERE id = @i", ("i", stale.Id)));
    }
}

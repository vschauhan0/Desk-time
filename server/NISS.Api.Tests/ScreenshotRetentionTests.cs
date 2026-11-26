using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;
using NISS.Application.Retention;
using NISS.Application.Screenshots;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class ScreenshotRetentionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Retention_deletes_expired_screenshots_files_and_rows()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-RET");
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IScreenshotStore>();
        var retention = scope.ServiceProvider.GetRequiredService<IRetentionService>();
        var d = await db.Devices.FirstAsync(x => x.DeviceCode == device.DeviceId);

        // A screenshot captured 20 days ago is older than the 14-day retention default.
        var old = await store.SaveAsync(
            new ScreenshotInput(d.Id, d.DeviceCode, d.EmployeeId, "alice", "ret-old",
                factory.Clock.GetUtcNow().AddDays(-20), 0, 100, 100),
            new byte[] { 1, 2, 3 }, default);
        var path = store.FullPath(old);
        Assert.True(File.Exists(path));

        await retention.RunAsync(default);

        Assert.False(File.Exists(path));
        Assert.False(await db.Screenshots.AnyAsync(s => s.Id == old.Id));
    }
}

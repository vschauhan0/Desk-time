using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;
using NISS.Application.Screenshots;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class ScreenshotStoreTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(Guid deviceId, string deviceCode, Guid? employeeId)> SeedDeviceAsync()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-SHOT");
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var d = await db.Devices.FirstAsync(x => x.DeviceCode == device.DeviceId);
        return (d.Id, d.DeviceCode, d.EmployeeId);
    }

    [Fact]
    public async Task SaveAsync_writes_file_and_row()
    {
        var (deviceId, deviceCode, employeeId) = await SeedDeviceAsync();
        using var scope = factory.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IScreenshotStore>();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var input = new ScreenshotInput(deviceId, deviceCode, employeeId, "alice", "frame-1",
            DateTimeOffset.UtcNow, Monitor: 0, Width: 1280, Height: 720);
        var jpeg = new byte[] { 1, 2, 3, 4, 5 };

        var shot = await store.SaveAsync(input, jpeg, default);

        Assert.True(File.Exists(store.FullPath(shot)));
        Assert.Equal(jpeg.Length, shot.ByteSize);
        Assert.Equal(0, shot.MonitorIndex);
        Assert.True(await db.Screenshots.AnyAsync(s => s.Id == shot.Id));
    }

    [Fact]
    public async Task DeleteAsync_removes_file_and_row()
    {
        var (deviceId, deviceCode, employeeId) = await SeedDeviceAsync();
        using var scope = factory.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IScreenshotStore>();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var shot = await store.SaveAsync(
            new ScreenshotInput(deviceId, deviceCode, employeeId, "alice", "frame-del", DateTimeOffset.UtcNow, 0, 100, 100),
            new byte[] { 9, 9, 9 }, default);
        var path = store.FullPath(shot);
        Assert.True(File.Exists(path));

        await store.DeleteAsync(new[] { shot }, default);

        Assert.False(File.Exists(path));
        Assert.False(await db.Screenshots.AnyAsync(s => s.Id == shot.Id));
    }
}

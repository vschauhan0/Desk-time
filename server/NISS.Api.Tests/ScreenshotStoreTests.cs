using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;
using NISS.Application.Screenshots;
using NISS.Monitor.Protocol;

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

/// <summary>Phase: configurable storage + employee-named folders/files.</summary>
[Collection(ApiCollection.Name)]
public sealed class ScreenshotStorageNamingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Path_is_employee_folder_then_date_then_named_file()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-NAME1");
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IScreenshotStore>();
        var d = await db.Devices.FirstAsync(x => x.DeviceCode == device.DeviceId);
        var emp = d.EmployeeId is null ? null : await db.Employees.FirstAsync(e => e.Id == d.EmployeeId);
        var at = new DateTimeOffset(2026, 11, 27, 9, 3, 27, TimeSpan.Zero);

        var shot = await store.SaveAsync(
            new ScreenshotInput(d.Id, d.DeviceCode, d.EmployeeId, "alice", "name-1", at, Monitor: -1, Width: 100, Height: 100,
                EmployeeName: emp?.FullName),
            [1, 2, 3], default);

        // <who>/<date>/<who>_<date>_<time>.jpg, where <who> is the employee folder (name) or the device code.
        var segments = shot.RelPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Equal(3, segments.Length);
        var local = at.ToLocalTime();
        Assert.Equal(local.ToString("yyyy-MM-dd"), segments[1]);                 // date folder
        Assert.StartsWith(segments[0] + "_" + local.ToString("yyyy-MM-dd") + "_", segments[2]); // name_date_time
        Assert.EndsWith(".jpg", segments[2]);
        Assert.True(File.Exists(store.FullPath(shot)));
    }

    [Fact]
    public async Task Clip_frames_get_distinct_named_files()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-NAME2");
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IScreenshotStore>();
        var d = await db.Devices.FirstAsync(x => x.DeviceCode == device.DeviceId);
        var at = DateTimeOffset.UtcNow;
        var clip = Guid.NewGuid();

        var f0 = await store.SaveAsync(new ScreenshotInput(d.Id, d.DeviceCode, d.EmployeeId, "alice", "c-0", at, -1, 100, 100, "FLAGGED", "facebook", "TITLE", clip, 0, 3), [1], default);
        var f1 = await store.SaveAsync(new ScreenshotInput(d.Id, d.DeviceCode, d.EmployeeId, "alice", "c-1", at, -1, 100, 100, "FLAGGED", "facebook", "TITLE", clip, 1, 3), [2], default);

        Assert.NotEqual(f0.RelPath, f1.RelPath);
        Assert.Contains("clip0000", f0.RelPath);
        Assert.Contains("clip0001", f1.RelPath);
        Assert.True(File.Exists(store.FullPath(f0)));
        Assert.True(File.Exists(store.FullPath(f1)));
    }

    [Fact]
    public async Task Storage_setting_round_trips_and_rejects_a_relative_path()
    {
        var admin = await factory.AdminAsync();

        var current = await admin.GetJsonAsync<ScreenshotStorageSettings>("/api/v1/settings/screenshot-storage");
        Assert.NotNull(current);

        var dir = Path.Combine(Path.GetTempPath(), "niss-store-" + Guid.NewGuid().ToString("N")[..8]);
        var saved = await (await admin.PutJsonAsync("/api/v1/settings/screenshot-storage", new ScreenshotStorageSettings(dir))).ReadAsync<ScreenshotStorageSettings>();
        Assert.Equal(dir, saved.Root);
        Assert.Equal(dir, (await admin.GetJsonAsync<ScreenshotStorageSettings>("/api/v1/settings/screenshot-storage")).Root);

        // A relative path is rejected.
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,
            (await admin.PutJsonAsync("/api/v1/settings/screenshot-storage", new ScreenshotStorageSettings(@"relativepath"))).StatusCode);

        // Restore default so other tests write to the usual location.
        await admin.PutJsonAsync("/api/v1/settings/screenshot-storage", new ScreenshotStorageSettings(""));
    }
}

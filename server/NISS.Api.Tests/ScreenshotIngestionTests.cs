using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class ScreenshotIngestionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static ByteArrayContent Jpeg(byte[] b) => new(b);

    private static string Url(string frameId, int monitor = 0, int w = 10, int h = 10, string user = "alice") =>
        $"/api/v1/devices/screenshots?frameId={frameId}&monitor={monitor}&width={w}&height={h}" +
        $"&capturedAtUtc={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"))}&sessionUser={user}";

    [Fact]
    public async Task Ingest_persists_screenshot()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-ING");

        var resp = await device.Client.PostAsync(Url("ing-1"), Jpeg(new byte[] { 1, 2, 3, 4 }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var d = await db.Devices.FirstAsync(x => x.DeviceCode == device.DeviceId);
        Assert.True(await db.Screenshots.AnyAsync(s => s.DeviceId == d.Id && s.FrameId == "ing-1"));
    }

    [Fact]
    public async Task Ingest_is_idempotent_on_frameId()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-ING2");

        await device.Client.PostAsync(Url("dup-1"), Jpeg(new byte[] { 1, 2, 3 }));
        await device.Client.PostAsync(Url("dup-1"), Jpeg(new byte[] { 1, 2, 3 }));

        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var d = await db.Devices.FirstAsync(x => x.DeviceCode == device.DeviceId);
        Assert.Equal(1, await db.Screenshots.CountAsync(s => s.DeviceId == d.Id && s.FrameId == "dup-1"));
    }

    [Fact]
    public async Task Ingest_rejects_when_not_device_authenticated()
    {
        using var anon = factory.CreateClient();

        var resp = await anon.PostAsync(Url("no-auth"), Jpeg(new byte[] { 1 }));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}

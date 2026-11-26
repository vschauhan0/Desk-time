using System.Net;
using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class ScreenshotQueryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string UploadUrl(string frameId) =>
        $"/api/v1/devices/screenshots?frameId={frameId}&monitor=0&width=10&height=10" +
        $"&capturedAtUtc={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"))}&sessionUser=alice";

    private async Task<(HttpClient admin, string deviceCode)> SeedWithShotAsync(string emp, string frameId)
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, emp);
        await device.Client.PostAsync(UploadUrl(frameId), new ByteArrayContent(new byte[] { 1, 2, 3, 4 }));
        return (admin, device.DeviceId);
    }

    [Fact]
    public async Task List_returns_metadata_in_range()
    {
        var (admin, deviceCode) = await SeedWithShotAsync("EMP-Q1", "q1");

        var page = await admin.GetJsonAsync<Paged<ScreenshotDto>>($"/api/v1/devices/{deviceCode}/screenshots");

        Assert.Contains(page.Items, s => s.Monitor == 0 && s.ByteSize == 4);
    }

    [Fact]
    public async Task Image_streams_bytes_and_audits()
    {
        var (admin, deviceCode) = await SeedWithShotAsync("EMP-Q2", "q2");
        var page = await admin.GetJsonAsync<Paged<ScreenshotDto>>($"/api/v1/devices/{deviceCode}/screenshots");
        var id = page.Items[0].Id;

        var resp = await admin.GetAsync($"/api/v1/devices/{deviceCode}/screenshots/{id}/image");
        var bytes = await resp.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/jpeg", resp.Content.Headers.ContentType!.MediaType);
        Assert.Equal(4, bytes.Length);
        var logs = await admin.GetJsonAsync<Paged<AuditLogDto>>("/api/v1/audit-logs?pageSize=500&action=SCREENSHOT_VIEWED");
        Assert.Contains(logs.Items, l => l.Target == deviceCode);
    }

    [Fact]
    public async Task Viewer_role_is_forbidden()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-Q3");
        await admin.PostJsonAsync("/api/v1/admin-users",
            new AdminUserUpsert("shot-viewer", "Shot Viewer", Roles.Viewer, true, "Shot-Viewer-Passw0rd!"));
        var (viewer, _) = await factory.LoginAsync("shot-viewer", "Shot-Viewer-Passw0rd!");

        var resp = await viewer.GetAsync($"/api/v1/devices/{device.DeviceId}/screenshots");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}

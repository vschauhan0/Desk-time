using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

/// <summary>A DEVICE_INVENTORY snapshot is projected to the latest-per-device inventory and served by the dashboard API.</summary>
[Collection(ApiCollection.Name)]
public sealed class InventoryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Device_inventory_snapshot_is_stored_and_served()
    {
        var admin = await factory.AdminAsync();
        await admin.PostJsonAsync("/api/v1/employees", new EmployeeUpsert("EMP-INV", "Ivy Inv", "IT", "ivy@example.test", true));
        var d = await factory.EnrollDeviceAsync(admin, "EMP-INV", user: "ivy");

        await d.IngestAsync(d.Event(EventTypes.DeviceInventory, DateTimeOffset.UtcNow, P.Inventory()));

        var inv = await admin.GetJsonAsync<DeviceInventoryDto>($"/api/v1/devices/{d.DeviceId}/inventory");
        Assert.Equal(d.DeviceId, inv.DeviceId);
        Assert.Equal("Dell Inc.", inv.Manufacturer);
        Assert.Equal("Latitude 7440", inv.Model);
        Assert.Equal("Windows 11 Pro", inv.OsName);
        Assert.Equal(16, inv.RamGb);
        Assert.Equal(10, inv.CpuCores);
        Assert.Contains(inv.Software, s => s.Name == "Google Chrome" && s.Version == "130.0" && s.Publisher == "Google LLC");
        Assert.Contains(inv.Software, s => s.Name == "7-Zip");
    }

    [Fact]
    public async Task Device_with_no_inventory_reported_returns_404()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, user: "noinv");
        var response = await admin.GetAsync($"/api/v1/devices/{d.DeviceId}/inventory");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}

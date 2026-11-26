using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class AgentScreenConfigTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Config_includes_capture_settings_defaulting_disabled()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, "EMP-CFG");

        var config = await device.Client.GetJsonAsync<AgentConfigResponse>("/api/v1/agent/config");

        Assert.NotNull(config.Policy.ScreenCapture);
        Assert.False(config.Policy.ScreenCapture.Enabled);
        Assert.Equal(60, config.Policy.ScreenCapture.IntervalSeconds);
        Assert.Equal(1600, config.Policy.ScreenCapture.MaxWidth);
    }
}

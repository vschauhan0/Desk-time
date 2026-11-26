using System.Text.Json;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Tests;

public class ScreenContractTests
{
    [Fact]
    public void ScreenFrameHeader_round_trips_through_protocol_json()
    {
        var header = new ScreenFrameHeader(ScreenPipeKind.Live, Monitor: 1, Width: 1280, Height: 720,
            CapturedAtUtcTicks: 638000000000000000L, ByteLength: 45678);

        var json = JsonSerializer.Serialize(header, ProtocolJson.Options);
        var back = JsonSerializer.Deserialize<ScreenFrameHeader>(json, ProtocolJson.Options);

        Assert.Equal(header, back);
        Assert.Contains("live", json); // enum serialized as camelCase string
    }

    [Fact]
    public void LiveControlMessage_round_trips_through_protocol_json()
    {
        var id = Guid.NewGuid();
        var msg = new LiveControlMessage(LiveControlAction.Start, id, Monitor: 0, Fps: 6, Quality: 50);

        var json = JsonSerializer.Serialize(msg, ProtocolJson.Options);
        var back = JsonSerializer.Deserialize<LiveControlMessage>(json, ProtocolJson.Options);

        Assert.Equal(msg, back);
        Assert.Equal("start", back!.Action);
    }

    [Fact]
    public void AgentPolicy_default_has_screen_capture_disabled()
    {
        var policy = new AgentPolicy();

        Assert.False(policy.ScreenCapture.Enabled);
        Assert.Equal(60, policy.ScreenCapture.IntervalSeconds);
        Assert.Equal(60, policy.ScreenCapture.Quality);
        Assert.Equal(1600, policy.ScreenCapture.MaxWidth);
        Assert.True(policy.ScreenCapture.LiveEnabled);
    }
}

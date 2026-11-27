using System.Text.Json;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

/// <summary>Wire contracts for the agent auto-update channel.</summary>
public sealed class UpdateContractTests
{
    [Fact]
    public void ReleaseManifest_round_trips_through_ProtocolJson()
    {
        var manifest = new ReleaseManifest(
            "1.4.0", "10.0.19041",
            new[] { new ManifestFile("agent/NISS.Agent.exe", "abc123"), new ManifestFile("watchdog/NISS.Watchdog.exe", "def456") },
            WatchdogChanged: true, Notes: "fixes");

        var json = JsonSerializer.Serialize(manifest, ProtocolJson.Options);
        var back = JsonSerializer.Deserialize<ReleaseManifest>(json, ProtocolJson.Options)!;

        Assert.Equal("1.4.0", back.Version);
        Assert.Equal("10.0.19041", back.MinOs);
        Assert.True(back.WatchdogChanged);
        Assert.Equal("fixes", back.Notes);
        Assert.Equal(2, back.Files.Count);
        Assert.Equal("agent/NISS.Agent.exe", back.Files[0].Path);
        Assert.Equal("def456", back.Files[1].Sha256);
    }

    [Fact]
    public void HeartbeatResponse_without_update_fields_still_deserializes()
    {
        const string legacy = """
            {"serverTimeUtc":"2026-11-27T10:00:00+00:00","deviceStatus":"ACTIVE","configVersion":3,
             "minAgentVersion":null,"latestAgentVersion":"1.3.0"}
            """;

        var r = JsonSerializer.Deserialize<HeartbeatResponse>(legacy, ProtocolJson.Options)!;

        Assert.Equal("ACTIVE", r.DeviceStatus);
        Assert.Equal(3, r.ConfigVersion);
        Assert.Null(r.TargetAgentVersion);
        Assert.Null(r.UpdateUrl);
    }

    [Fact]
    public void HeartbeatResponse_carries_target_version_and_url()
    {
        var r = new HeartbeatResponse(DateTimeOffset.UtcNow, "ACTIVE", 1, null, null, "1.4.0", "/api/v1/agent/update/1.4.0");
        var back = JsonSerializer.Deserialize<HeartbeatResponse>(JsonSerializer.Serialize(r, ProtocolJson.Options), ProtocolJson.Options)!;
        Assert.Equal("1.4.0", back.TargetAgentVersion);
        Assert.Equal("/api/v1/agent/update/1.4.0", back.UpdateUrl);
    }

    [Fact]
    public void Update_constants_have_expected_wire_values()
    {
        Assert.Equal("UPDATE_FAILED", SecurityEventKinds.UpdateFailed);
        Assert.Equal("UPDATE_RESTART", StopReasons.UpdateRestart);
    }
}

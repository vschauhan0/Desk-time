using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Monitor.Protocol;

namespace NISS.Application.Settings;

/// <summary>Audited administration of retention and health-threshold settings.</summary>
public interface ISettingsAdminService
{
    Task<RetentionSettings> GetRetentionAsync(CancellationToken ct);
    Task<RetentionSettings> UpdateRetentionAsync(RetentionSettings value, CancellationToken ct);
    Task<HealthThresholds> GetThresholdsAsync(CancellationToken ct);
    Task<HealthThresholds> UpdateThresholdsAsync(HealthThresholds value, CancellationToken ct);
}

internal sealed class SettingsAdminService(ISettingsService settings, IAuditService audit) : ISettingsAdminService
{
    public Task<RetentionSettings> GetRetentionAsync(CancellationToken ct) => settings.GetRetentionAsync(ct);

    public async Task<RetentionSettings> UpdateRetentionAsync(RetentionSettings value, CancellationToken ct)
    {
        await settings.SetRetentionAsync(value, ct);
        await audit.WriteAsync(AuditActions.SettingsUpdated, "retention",
            $"raw={value.RawEventsDays}d, applications={value.ApplicationSummariesDays}d, health={value.DeviceHealthDays}d, audit={value.AuditLogsDays}d", ct);
        return value;
    }

    public Task<HealthThresholds> GetThresholdsAsync(CancellationToken ct) => settings.GetThresholdsAsync(ct);

    public async Task<HealthThresholds> UpdateThresholdsAsync(HealthThresholds value, CancellationToken ct)
    {
        var normalized = value with { MinAgentVersion = string.IsNullOrWhiteSpace(value.MinAgentVersion) ? null : value.MinAgentVersion.Trim() };
        await settings.SetThresholdsAsync(normalized, ct);
        await audit.WriteAsync(AuditActions.SettingsUpdated, "health-thresholds",
            $"warning={normalized.HeartbeatWarningSeconds}s, offline={normalized.OfflineSeconds}s, backlog={normalized.BacklogWarningThreshold}, minVersion={normalized.MinAgentVersion ?? "-"}", ct);
        return normalized;
    }
}

using FluentValidation;
using NISS.Application.Categories;
using NISS.Application.Ingestion;
using NISS.Domain;
using NISS.Monitor.Protocol;

namespace NISS.Application.Validation;

public sealed class EnrollRequestValidator : AbstractValidator<EnrollRequest>
{
    public EnrollRequestValidator()
    {
        RuleFor(x => x.EnrollmentToken).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Hostname).NotEmpty().MaximumLength(255);
        RuleFor(x => x.WindowsUsername).NotEmpty().MaximumLength(256);
        RuleFor(x => x.AgentVersion).NotEmpty().MaximumLength(64);
        RuleFor(x => x.MachineGuid).MaximumLength(64);
        RuleFor(x => x.MachineSerial).MaximumLength(128);
        RuleFor(x => x.EmployeeId).MaximumLength(64);
        RuleFor(x => x.OsVersion).MaximumLength(256);
        RuleFor(x => x.Manufacturer).MaximumLength(128);
        RuleFor(x => x.Model).MaximumLength(128);
    }
}

public sealed class EventBatchRequestValidator : AbstractValidator<EventBatchRequest>
{
    public EventBatchRequestValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty().MaximumLength(32);
        RuleFor(x => x.Events).NotNull().WithMessage("events is required");
        RuleFor(x => x.Events.Count).LessThanOrEqualTo(EventIngestionLimits.MaxBatchSize)
            .When(x => x.Events is not null).WithName("events").WithMessage($"A batch may contain at most {EventIngestionLimits.MaxBatchSize} events.");
    }
}

/// <summary>Public limits so the API and validators agree with the ingestion service.</summary>
public static class EventIngestionLimits
{
    public const int MaxBatchSize = 500;
}

public sealed class HeartbeatRequestValidator : AbstractValidator<HeartbeatRequest>
{
    public HeartbeatRequestValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty().MaximumLength(32);
        RuleFor(x => x.AgentVersion).NotEmpty().MaximumLength(64);
        RuleFor(x => x.TimestampUtc).NotEqual(default(DateTimeOffset));
        RuleFor(x => x.UptimeSeconds).GreaterThanOrEqualTo(0);
        RuleFor(x => x.QueueDepth).GreaterThanOrEqualTo(0);
        RuleFor(x => x.ServiceState).MaximumLength(32);
        RuleFor(x => x.SessionAgents).NotNull();
        RuleFor(x => x.LastError).MaximumLength(2000);
        RuleFor(x => x.Live!.Presence).NotEmpty().MaximumLength(32).When(x => x.Live is not null);
    }
}

public sealed class AgentStatusRequestValidator : AbstractValidator<AgentStatusRequest>
{
    public AgentStatusRequestValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty().MaximumLength(32);
        RuleFor(x => x.AgentVersion).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Collectors).NotNull();
        RuleFor(x => x.SessionAgents).NotNull();
        RuleFor(x => x.QueueDepth).GreaterThanOrEqualTo(0);
        RuleFor(x => x.DeadLetterCount).GreaterThanOrEqualTo(0);
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
    }
}

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(256);
}

public sealed class AdminUserUpsertValidator : AbstractValidator<AdminUserUpsert>
{
    public AdminUserUpsertValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MaximumLength(64).Matches("^[A-Za-z0-9._@-]+$").WithMessage("Username may contain letters, digits and . _ @ -");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Role).Must(r => r is not null && (r.ToUpperInvariant() is Roles.SuperAdmin or Roles.Admin or Roles.Viewer))
            .WithMessage("Role must be SUPERADMIN, ADMIN or VIEWER.");
        RuleFor(x => x.Password!).MinimumLength(10).MaximumLength(128)
            .Must((x, p) => !string.Equals(p, x.Username, StringComparison.OrdinalIgnoreCase)).WithMessage("Password must not equal the username.")
            .When(x => !string.IsNullOrEmpty(x.Password)).WithName("password");
    }
}

public sealed class EmployeeUpsertValidator : AbstractValidator<EmployeeUpsert>
{
    public EmployeeUpsertValidator()
    {
        RuleFor(x => x.EmployeeCode).NotEmpty().MaximumLength(64);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Department).MaximumLength(128);
        RuleFor(x => x.Email).MaximumLength(256).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
    }
}

public sealed class EnrollmentTokenRequestValidator : AbstractValidator<EnrollmentTokenRequest>
{
    public EnrollmentTokenRequestValidator()
    {
        RuleFor(x => x.ExpiresInHours).InclusiveBetween(1, 24 * 30);
        RuleFor(x => x.EmployeeCode).MaximumLength(64);
        RuleFor(x => x.DeviceId).MaximumLength(32);
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

public sealed class AgentPolicyValidator : AbstractValidator<AgentPolicy>
{
    public AgentPolicyValidator()
    {
        RuleFor(x => x.HeartbeatSeconds).InclusiveBetween(5, 3600);
        RuleFor(x => x.MetricsSeconds).InclusiveBetween(5, 3600);
        RuleFor(x => x.ForegroundPollMilliseconds).InclusiveBetween(200, 10_000);
        RuleFor(x => x.IdleThresholdMinutes).InclusiveBetween(1, 240);
        RuleFor(x => x.InteractionWindowSeconds).InclusiveBetween(5, 600);
        RuleFor(x => x.InputBucketSeconds).InclusiveBetween(5, 600);
        RuleFor(x => x.UploadBatchSize).InclusiveBetween(1, EventIngestionLimits.MaxBatchSize);
        RuleFor(x => x.UploadIntervalSeconds).InclusiveBetween(1, 600);
        RuleFor(x => x.BacklogWarningThreshold).InclusiveBetween(1, 1_000_000);
        RuleFor(x => x.ProcessMonitoringMode).Must(m => m is "Interactive" or "All").WithMessage("ProcessMonitoringMode must be Interactive or All.");
        RuleFor(x => x.ProcessExclusions).NotNull();
        RuleFor(x => x.FileMonitoring).NotNull();
        RuleFor(x => x.CursorSampling).NotNull();
        RuleFor(x => x.CursorSampling.IntervalSeconds).InclusiveBetween(1, 3600).When(x => x.CursorSampling is not null);
    }
}

public sealed class PolicyUpsertValidator : AbstractValidator<PolicyUpsert>
{
    public PolicyUpsertValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Scope).Must(s => Enum.TryParse<PolicyScope>(s, true, out _)).WithMessage("Scope must be GLOBAL, DEPARTMENT or DEVICE.");
        RuleFor(x => x.ScopeValue).MaximumLength(128);
        RuleFor(x => x.Policy).NotNull().SetValidator(new AgentPolicyValidator());
    }
}

public sealed class AppCategoryRuleValidator : AbstractValidator<AppCategoryRule>
{
    public AppCategoryRuleValidator()
    {
        RuleFor(x => x.Pattern).NotEmpty().MaximumLength(100).Matches(@"^[^\\/:""<>|]+$").WithMessage("Pattern is a simple wildcard such as chrome* (no path separators).");
        RuleFor(x => x.Category).Must(c => AppCategories.All.Contains(c, StringComparer.OrdinalIgnoreCase)).WithMessage("Unknown category.");
        RuleFor(x => x.Priority).InclusiveBetween(-1000, 1000);
    }
}

public sealed class AppCategoryRuleListValidator : AbstractValidator<List<AppCategoryRule>>
{
    public AppCategoryRuleListValidator()
    {
        RuleFor(x => x.Count).LessThanOrEqualTo(1000).WithName("rules");
        RuleForEach(x => x).SetValidator(new AppCategoryRuleValidator());
    }
}

public sealed class RetentionSettingsValidator : AbstractValidator<RetentionSettings>
{
    public RetentionSettingsValidator()
    {
        RuleFor(x => x.RawEventsDays).InclusiveBetween(0, 3650);
        RuleFor(x => x.ApplicationSummariesDays).InclusiveBetween(0, 3650);
        RuleFor(x => x.DeviceHealthDays).InclusiveBetween(0, 3650);
        RuleFor(x => x.AuditLogsDays).InclusiveBetween(0, 3650);
    }
}

public sealed class HealthThresholdsValidator : AbstractValidator<HealthThresholds>
{
    public HealthThresholdsValidator()
    {
        RuleFor(x => x.HeartbeatWarningSeconds).InclusiveBetween(5, 3600);
        RuleFor(x => x.OfflineSeconds).InclusiveBetween(10, 86_400).GreaterThan(x => x.HeartbeatWarningSeconds);
        RuleFor(x => x.BacklogWarningThreshold).InclusiveBetween(1, 1_000_000);
        RuleFor(x => x.MinAgentVersion).MaximumLength(32).Matches(@"^\d+(\.\d+){0,3}([-+].*)?$").When(x => !string.IsNullOrWhiteSpace(x.MinAgentVersion))
            .WithMessage("MinAgentVersion must look like 1.2.3.");
    }
}

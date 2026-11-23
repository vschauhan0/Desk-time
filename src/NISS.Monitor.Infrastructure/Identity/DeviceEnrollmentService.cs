using Microsoft.Extensions.Logging;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Infrastructure.Http;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Identity;

/// <summary>Enrolls the device with a one-time token and manages credential rotation.</summary>
public sealed class DeviceEnrollmentService : IDeviceEnrollmentService
{
    private readonly ServerApiClient _api;
    private readonly DeviceIdentityStore _store;
    private readonly MachineInfo _machine;
    private readonly ServerTrust _trust;
    private readonly IPolicyProvider _policy;
    private readonly TimeProvider _time;
    private readonly ILogger<DeviceEnrollmentService> _log;

    public DeviceEnrollmentService(ServerApiClient api, DeviceIdentityStore store, MachineInfo machine, ServerTrust trust,
        IPolicyProvider policy, TimeProvider time, ILogger<DeviceEnrollmentService> log)
    {
        _api = api;
        _store = store;
        _machine = machine;
        _trust = trust;
        _policy = policy;
        _time = time;
        _log = log;
    }

    /// <inheritdoc />
    public Task<DeviceIdentity?> GetIdentityAsync(CancellationToken cancellationToken) => Task.FromResult(_store.Reload());

    /// <inheritdoc />
    public async Task<DeviceIdentity> EnrollAsync(EnrollmentRequestOptions options, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.CertificateThumbprint)) _trust.Thumbprint = options.CertificateThumbprint;

        var request = new EnrollRequest(
            options.EnrollmentToken, _machine.Hostname, _machine.MachineGuid, _machine.Serial, options.EmployeeId,
            _machine.InteractiveUserOrCurrent(), AgentVersion.Current, _machine.OsVersion, _machine.Manufacturer, _machine.Model);

        var result = await _api.EnrollAsync(options.ServerBaseUrl, request, cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.Value is null)
            throw new InvalidOperationException($"Enrollment failed: {result.Error ?? "no response"}");

        var identity = new DeviceIdentity(result.Value.DeviceId, result.Value.DeviceSecret, options.ServerBaseUrl.TrimEnd('/'),
            result.Value.EmployeeId ?? options.EmployeeId, _trust.Thumbprint, _time.GetUtcNow());
        _store.Save(identity);
        await _policy.UpdateAsync(result.Value.Policy, cancellationToken).ConfigureAwait(false);
        _log.LogInformation("Device enrolled as {DeviceId}", identity.DeviceId);
        return identity;
    }

    /// <inheritdoc />
    public async Task RotateCredentialsAsync(CancellationToken cancellationToken)
    {
        var result = await _api.RotateAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.Value is null)
            throw new InvalidOperationException($"Credential rotation failed: {result.Error}");
        _store.UpdateSecret(result.Value.DeviceSecret);
        _log.LogInformation("Device credential rotated");
    }
}

/// <summary>Agent version helper.</summary>
public static class AgentVersion
{
    /// <summary>Informational version of the executing assembly (e.g. 1.0.0).</summary>
    public static string Current { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var asm = typeof(AgentVersion).Assembly;
        var v = asm.GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}

using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Application.Settings;
using NISS.Domain;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Policies;

/// <summary>Resolves and administers configuration policies (scope precedence global &lt; department &lt; device).</summary>
public interface IPolicyService
{
    /// <summary>The policy an agent should run with. The <c>Version</c> is the server-wide policy version.</summary>
    Task<AgentPolicy> GetEffectiveAsync(string deviceCode, string? department, CancellationToken ct);

    Task<IReadOnlyList<PolicyDto>> ListAsync(CancellationToken ct);

    /// <summary>Creates or replaces the policy with the given id.</summary>
    Task<PolicyDto> UpsertAsync(Guid id, PolicyUpsert request, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);
}

internal sealed class PolicyService(IAppDbContext db, ISettingsService settings, IAuditService audit, TimeProvider clock) : IPolicyService
{
    public async Task<AgentPolicy> GetEffectiveAsync(string deviceCode, string? department, CancellationToken ct)
    {
        var dept = department?.Trim() ?? "";
        var candidates = await db.ConfigurationPolicies.AsNoTracking()
            .Where(p => p.Scope == PolicyScope.Global
                     || (p.Scope == PolicyScope.Device && p.ScopeValue == deviceCode)
                     || (p.Scope == PolicyScope.Department && dept != "" && p.ScopeValue.ToLower() == dept.ToLower()))
            .ToListAsync(ct);

        var chosen = candidates.OrderByDescending(p => (int)p.Scope).FirstOrDefault();
        var policy = JsonColumn.Deserialize<AgentPolicy>(chosen?.PolicyJson) ?? new AgentPolicy();
        var thresholds = await settings.GetThresholdsAsync(ct);
        var version = await settings.GetPolicyVersionAsync(ct);
        return policy with { Version = version, MinAgentVersion = policy.MinAgentVersion ?? thresholds.MinAgentVersion };
    }

    public async Task<IReadOnlyList<PolicyDto>> ListAsync(CancellationToken ct)
    {
        var rows = await db.ConfigurationPolicies.AsNoTracking().ToListAsync(ct);
        var version = await settings.GetPolicyVersionAsync(ct);
        return rows.OrderBy(r => (int)r.Scope).ThenBy(r => r.ScopeValue).Select(r => ToDto(r, version)).ToList();
    }

    public async Task<PolicyDto> UpsertAsync(Guid id, PolicyUpsert request, CancellationToken ct)
    {
        if (!Enum.TryParse<PolicyScope>(request.Scope, ignoreCase: true, out var scope))
            throw new BadRequestException("Scope must be GLOBAL, DEPARTMENT or DEVICE.", nameof(request.Scope));
        var scopeValue = scope == PolicyScope.Global ? "" : (request.ScopeValue ?? "").Trim();
        if (scope != PolicyScope.Global && scopeValue.Length == 0)
            throw new BadRequestException("ScopeValue is required for DEPARTMENT and DEVICE policies.", nameof(request.ScopeValue));

        var row = await db.ConfigurationPolicies.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is not null && row.Scope == PolicyScope.Global && scope != PolicyScope.Global)
            throw new ConflictException("The global policy cannot change scope.");
        if (scope == PolicyScope.Global && await db.ConfigurationPolicies.AnyAsync(p => p.Scope == PolicyScope.Global && p.Id != id, ct))
            throw new ConflictException("There is exactly one GLOBAL policy; update it instead of creating another.");
        if (await db.ConfigurationPolicies.AnyAsync(p => p.Scope == scope && p.ScopeValue == scopeValue && p.Id != id, ct))
            throw new ConflictException("A policy for this scope already exists.");

        var now = clock.GetUtcNow();
        if (row is null)
        {
            row = new ConfigurationPolicy { Id = id == Guid.Empty ? Guid.NewGuid() : id };
            db.ConfigurationPolicies.Add(row);
        }
        row.Name = request.Name.Trim();
        row.Scope = scope;
        row.ScopeValue = scopeValue;
        row.PolicyJson = JsonColumn.Serialize(request.Policy) ?? "{}";
        row.UpdatedAt = now;
        audit.Record("POLICY_UPDATED", $"{scope}:{scopeValue}".TrimEnd(':'), row.Name);
        await db.SaveChangesAsync(ct);
        var version = await settings.BumpPolicyVersionAsync(ct);
        return ToDto(row, version);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var row = await db.ConfigurationPolicies.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Policy not found.");
        if (row.Scope == PolicyScope.Global) throw new ConflictException("The global policy cannot be deleted.");
        db.ConfigurationPolicies.Remove(row);
        audit.Record("POLICY_DELETED", $"{row.Scope}:{row.ScopeValue}", row.Name);
        await db.SaveChangesAsync(ct);
        await settings.BumpPolicyVersionAsync(ct);
    }

    private static PolicyDto ToDto(ConfigurationPolicy r, int version)
    {
        var policy = JsonColumn.Deserialize<AgentPolicy>(r.PolicyJson) ?? new AgentPolicy();
        return new PolicyDto(r.Id, r.Name, r.Scope.ToString().ToUpperInvariant(),
            r.Scope == PolicyScope.Global ? null : r.ScopeValue, policy with { Version = version }, r.UpdatedAt);
    }
}

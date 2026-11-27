using NISS.Monitor.Setup;

namespace NISS.Monitor.Tests;

/// <summary>
/// Pins the intent of the hardened service DACL (layer-2 anti-tamper): administrators lose STOP and DELETE, but
/// SYSTEM keeps full control and admins keep enough to re-assert control deliberately. The real "does Windows
/// actually deny the stop" behaviour needs an elevated real PC and is covered by the manual install test.
/// </summary>
public sealed class ServiceHardeningTests
{
    // Rights field of the DACL ACE granted to a trustee short-name (SY, BA, IU, SU), split into 2-char tokens.
    private static string[] AceRights(string sddl, string trustee)
    {
        foreach (var ace in sddl.Split('('))
        {
            var fields = ace.TrimEnd(')').Split(';');
            if (fields.Length >= 6 && fields[5] == trustee)
                return Chunk2(fields[2]);
        }
        return [];
    }

    private static string[] Chunk2(string rights)
    {
        var list = new List<string>();
        for (var i = 0; i + 1 < rights.Length; i += 2) list.Add(rights.Substring(i, 2));
        return [.. list];
    }

    [Fact]
    public void Default_sddl_lets_administrators_stop_and_delete()
    {
        var ba = AceRights(AgentSetup.DefaultServiceSddl, "BA");
        Assert.Contains("WP", ba); // SERVICE_STOP
        Assert.Contains("SD", ba); // DELETE
    }

    [Fact]
    public void Hardened_sddl_denies_administrators_stop_and_delete()
    {
        var ba = AceRights(AgentSetup.HardenedServiceSddl, "BA");
        Assert.DoesNotContain("WP", ba); // admins can no longer stop it
        Assert.DoesNotContain("SD", ba); // admins can no longer delete it
    }

    [Fact]
    public void Hardened_sddl_keeps_administrators_able_to_reconfigure_and_reassert_control()
    {
        // KEEP change-config + write-DAC so the official uninstaller/repair and a deliberate admin still work:
        // a speed bump plus detection, not a wall (docs/security.md, docs/least-privilege-rollout.md).
        var ba = AceRights(AgentSetup.HardenedServiceSddl, "BA");
        Assert.Contains("DC", ba); // SERVICE_CHANGE_CONFIG
        Assert.Contains("WD", ba); // WRITE_DAC
    }

    [Fact]
    public void Hardened_sddl_keeps_system_in_full_control()
    {
        // SYSTEM must keep STOP/START/DELETE so SCM recovery actions and the peer watchdog still function.
        var sy = AceRights(AgentSetup.HardenedServiceSddl, "SY");
        Assert.Contains("WP", sy);
        Assert.Contains("RP", sy);
        Assert.Contains("SD", sy);
    }

    [Fact]
    public void Hardened_sddl_is_dacl_only_so_no_SeSecurityPrivilege_is_needed()
    {
        // Setting a SACL via `sc sdset` requires SeSecurityPrivilege; we touch only the DACL on purpose.
        Assert.StartsWith("D:", AgentSetup.HardenedServiceSddl);
        Assert.DoesNotContain("S:", AgentSetup.HardenedServiceSddl);
    }
}

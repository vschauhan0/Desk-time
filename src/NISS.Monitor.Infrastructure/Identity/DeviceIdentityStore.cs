using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NISS.Monitor.Core;
using NISS.Monitor.Infrastructure.Persistence;

namespace NISS.Monitor.Infrastructure.Identity;

/// <summary>
/// Persists the device identity in agent.db. The per-device secret is encrypted with machine-scope DPAPI so a
/// copy of the database file is useless on another machine, and it is never written to logs.
/// </summary>
public sealed class DeviceIdentityStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NISS.Monitor.Agent.DeviceSecret.v1");
    private readonly SqliteDatabase _db;
    private volatile DeviceIdentity? _cached;
    private volatile bool _loaded;

    public DeviceIdentityStore(SqliteDatabase db) => _db = db;

    /// <summary>Current identity (loaded lazily from the database).</summary>
    public DeviceIdentity? Current
    {
        get
        {
            if (!_loaded) Reload();
            return _cached;
        }
    }

    /// <summary>Re-read from the database (e.g. after <c>NISSAgent --enroll</c> ran in another process).</summary>
    public DeviceIdentity? Reload()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT device_id, secret_protected, server_base_url, employee_id, cert_thumbprint, enrolled_utc FROM device_identity WHERE id = 1;";
        using var r = cmd.ExecuteReader();
        DeviceIdentity? id = null;
        if (r.Read())
        {
            try
            {
                var secret = Encoding.UTF8.GetString(ProtectedData.Unprotect((byte[])r["secret_protected"], Entropy, DataProtectionScope.LocalMachine));
                id = new DeviceIdentity(r.GetString(0), secret, r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
            }
            catch (CryptographicException)
            {
                id = null; // database copied from another machine: treat as not enrolled
            }
        }
        _cached = id;
        _loaded = true;
        return id;
    }

    /// <summary>Store (or replace) the identity.</summary>
    public void Save(DeviceIdentity identity)
    {
        var protectedSecret = ProtectedData.Protect(Encoding.UTF8.GetBytes(identity.DeviceSecret), Entropy, DataProtectionScope.LocalMachine);
        using var c = _db.Open();
        SqliteDatabase.Exec(c, """
            INSERT INTO device_identity(id, device_id, secret_protected, server_base_url, employee_id, cert_thumbprint, enrolled_utc)
            VALUES(1,$d,$s,$u,$e,$t,$at)
            ON CONFLICT(id) DO UPDATE SET device_id=$d, secret_protected=$s, server_base_url=$u, employee_id=$e, cert_thumbprint=$t, enrolled_utc=$at;
            """, ("$d", identity.DeviceId), ("$s", protectedSecret), ("$u", identity.ServerBaseUrl), ("$e", identity.EmployeeId),
            ("$t", identity.CertificateThumbprint), ("$at", identity.EnrolledAtUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)));
        _cached = identity;
        _loaded = true;
    }

    /// <summary>Update only the secret (rotation).</summary>
    public void UpdateSecret(string newSecret)
    {
        var id = Current ?? throw new InvalidOperationException("Device is not enrolled.");
        Save(id with { DeviceSecret = newSecret });
    }

    /// <summary>Forget the identity (used by uninstall --purge and re-enrollment).</summary>
    public void Clear()
    {
        using var c = _db.Open();
        SqliteDatabase.Exec(c, "DELETE FROM device_identity;");
        _cached = null;
        _loaded = true;
    }
}

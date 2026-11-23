namespace NISS.Domain;

/// <summary>Scope of a configuration policy. Precedence: <see cref="Global"/> &lt; <see cref="Department"/> &lt; <see cref="Device"/>.</summary>
public enum PolicyScope
{
    /// <summary>Applies to every device (exactly one row).</summary>
    Global = 0,
    /// <summary>Applies to devices whose employee belongs to a department (scope value = department name).</summary>
    Department = 1,
    /// <summary>Applies to a single device (scope value = device code such as PC-024).</summary>
    Device = 2,
}

/// <summary>Severity of a security / agent-health event.</summary>
public enum SecuritySeverity
{
    /// <summary>Informational.</summary>
    Info = 0,
    /// <summary>Needs attention.</summary>
    Warning = 1,
    /// <summary>Needs prompt attention.</summary>
    Critical = 2,
}

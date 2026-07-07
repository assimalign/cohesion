using System;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// The version of the Cohesion database wire protocol negotiated during startup.
/// </summary>
/// <param name="Major">The major version; incompatible framing changes increment this.</param>
/// <param name="Minor">The minor version; additive message changes increment this.</param>
public readonly record struct ProtocolVersion(ushort Major, ushort Minor) : IComparable<ProtocolVersion>
{
    /// <summary>
    /// Gets the current protocol version implemented by this assembly.
    /// </summary>
    public static ProtocolVersion Current => new(1, 0);

    /// <inheritdoc />
    public int CompareTo(ProtocolVersion other)
    {
        var major = Major.CompareTo(other.Major);
        return major != 0 ? major : Minor.CompareTo(other.Minor);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Major}.{Minor}";
}

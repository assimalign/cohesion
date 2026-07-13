using System;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The version of the Cohesion database wire protocol negotiated during startup.
/// </summary>
/// <param name="Major">The major version; incompatible framing changes increment this.</param>
/// <param name="Minor">The minor version; additive message changes increment this.</param>
/// <remarks>
/// The value type lives in the area root because it is part of the
/// <see cref="IDatabaseServerSession"/> vocabulary; the version the wire
/// implementation actually speaks is published by the protocol package as
/// <c>ProtocolVersion.Current</c> (a static extension member in
/// <c>Assimalign.Cohesion.Database.Protocol</c>).
/// </remarks>
public readonly record struct ProtocolVersion(ushort Major, ushort Minor) : IComparable<ProtocolVersion>
{
    /// <inheritdoc />
    public int CompareTo(ProtocolVersion other)
    {
        var major = Major.CompareTo(other.Major);
        return major != 0 ? major : Minor.CompareTo(other.Minor);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Major}.{Minor}";
}

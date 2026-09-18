using System;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// The version of the Cohesion database wire protocol negotiated during startup.
/// </summary>
/// <param name="Major">The major version; incompatible wire changes increment this.</param>
/// <param name="Minor">The minor version; negotiated additive capabilities increment this.</param>
/// <remarks>
/// The value type lives with the wire implementation that defines it (this package);
/// the area root consumes it for the <c>IDatabaseServerSession</c> vocabulary through
/// the root's child-root reference. <see cref="Current"/> is the version this
/// assembly implements.
/// </remarks>
public readonly record struct ProtocolVersion(ushort Major, ushort Minor) : IComparable<ProtocolVersion>
{
    /// <summary>
    /// Gets the current protocol version implemented by this assembly.
    /// </summary>
    public static ProtocolVersion Current => new(1, 0);

    /// <summary>Negotiates the common version while rejecting incompatible major versions.</summary>
    /// <param name="requested">The peer's requested version.</param>
    /// <param name="negotiated">The supported version, or default on rejection.</param>
    /// <returns>True when the requested major version is supported.</returns>
    public static bool TryNegotiate(ProtocolVersion requested, out ProtocolVersion negotiated)
    {
        negotiated = default;
        if (requested.Major != Current.Major)
        {
            return false;
        }
        negotiated = new ProtocolVersion(Current.Major, Math.Min(requested.Minor, Current.Minor));
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(ProtocolVersion other)
    {
        var major = Major.CompareTo(other.Major);
        return major != 0 ? major : Minor.CompareTo(other.Minor);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Major}.{Minor}";
}

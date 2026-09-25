using System;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A DNS question (the <c>QNAME</c>/<c>QTYPE</c>/<c>QCLASS</c> triple from RFC 1035 &#167; 4.1.2).
/// Used as the input to <see cref="IDnsClient"/> and <see cref="IDnsResolver"/> queries and as
/// the first section of a <see cref="DnsMessage"/>.
/// </summary>
/// <remarks>
/// <see cref="DnsQuestion"/> is a readonly value type so it can be passed cheaply through the
/// query pipeline without allocation. The default value represents an unset question
/// (<see cref="Name"/> defaults to the root, <see cref="Type"/> defaults to <c>0</c>) and is
/// not a valid query &#8211; callers must initialize all three fields.
/// </remarks>
public readonly struct DnsQuestion : IEquatable<DnsQuestion>
{
    /// <summary>
    /// Initializes a new question for <paramref name="name"/> of type <paramref name="type"/>.
    /// </summary>
    /// <param name="name">The domain name to look up.</param>
    /// <param name="type">The resource-record type requested.</param>
    /// <param name="class">The DNS class. Defaults to <see cref="DnsClass.IN"/> &#8211; the only
    /// class in widespread use.</param>
    public DnsQuestion(DnsName name, DnsRecordType type, DnsClass @class = DnsClass.IN)
    {
        Name = name;
        Type = type;
        Class = @class;
    }

    /// <summary>The domain name being queried.</summary>
    public DnsName Name { get; }

    /// <summary>The resource-record type being requested.</summary>
    public DnsRecordType Type { get; }

    /// <summary>The DNS class. Almost always <see cref="DnsClass.IN"/>.</summary>
    public DnsClass Class { get; }

    /// <inheritdoc />
    public bool Equals(DnsQuestion other)
        => Name.Equals(other.Name) && Type == other.Type && Class == other.Class;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DnsQuestion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, (int)Type, (int)Class);

    /// <inheritdoc />
    public override string ToString() => $"{Name} {Class} {Type}";

    public static bool operator ==(DnsQuestion left, DnsQuestion right) => left.Equals(right);
    public static bool operator !=(DnsQuestion left, DnsQuestion right) => !left.Equals(right);
}

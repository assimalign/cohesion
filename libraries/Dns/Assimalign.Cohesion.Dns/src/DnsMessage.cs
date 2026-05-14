using System.Collections.Generic;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A complete DNS message &#8211; header plus question, answer, authority, and additional
/// sections (RFC 1035 &#167; 4.1).
/// </summary>
/// <remarks>
/// <para>
/// PR 1 ships only the public surface so the contract layer can compile. The wire-format
/// implementation (read/write of the binary representation, name compression, EDNS handling)
/// lands in PR 2 alongside the resource-record family.
/// </para>
/// <para>
/// Until then, callers can hold a <see cref="DnsMessage"/> reference but the only meaningful
/// member is <see cref="Questions"/>. Sealed against subclassing so the wire-format layer can
/// rely on a known shape.
/// </para>
/// </remarks>
public sealed class DnsMessage
{
    /// <summary>
    /// Initializes a new <see cref="DnsMessage"/> with the supplied identifier and question.
    /// </summary>
    public DnsMessage(ushort id, DnsQuestion question)
    {
        Id = id;
        Questions = new[] { question };
    }

    /// <summary>The 16-bit transaction identifier from the DNS header.</summary>
    public ushort Id { get; }

    /// <summary>The questions section. Most messages carry exactly one question.</summary>
    public IReadOnlyList<DnsQuestion> Questions { get; }
}

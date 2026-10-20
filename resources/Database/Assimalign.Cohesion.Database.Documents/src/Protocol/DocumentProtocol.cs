using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>The document endpoint's immutable protocol family.</summary>
public static class DocumentProtocol
{
    /// <summary>Gets the message family bound to each document connection.</summary>
    public static ProtocolMessageFamily Family { get; } = new("Documents", 64, 65, 66, 67, 68, 69, 70, 71, 72);
}

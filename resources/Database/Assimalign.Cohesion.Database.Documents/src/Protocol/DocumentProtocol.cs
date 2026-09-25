using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>The document endpoint's immutable protocol family.</summary>
public static class DocumentProtocol
{
    /// <summary>Gets the message family bound to each document connection.</summary>
    public static ProtocolMessageFamily Family { get; } = new("Documents", 64, 65, 66);
}

/// <summary>Message identifiers scoped to document endpoints.</summary>
public enum DocumentProtocolMessageType : byte
{
    /// <summary>Client request carrying OQL and a JSON parameter object.</summary>
    Execute = 64,
    /// <summary>One complete JSON result, preserving nested objects and arrays.</summary>
    Document = 65,
    /// <summary>Successful end of a result sequence, carrying its result count.</summary>
    Complete = 66,
}

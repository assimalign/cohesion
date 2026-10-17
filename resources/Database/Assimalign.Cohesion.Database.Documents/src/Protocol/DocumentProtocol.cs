using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>The document endpoint's immutable protocol family.</summary>
public static class DocumentProtocol
{
    /// <summary>Gets the message family bound to each document connection.</summary>
    public static ProtocolMessageFamily Family { get; } = new("Documents", 64, 65, 66, 67, 68, 69, 70, 71, 72);
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
    /// <summary>Column names and types preceding query results.</summary>
    ResultHeader = 67,
    /// <summary>Reserved identifier for retired projection arrays; not used by OQL execution.</summary>
    ResultRow = 68,
    /// <summary>A document or collection operation.</summary>
    Command = 69,
    /// <summary>A stored document with its identity and version.</summary>
    StoredDocument = 70,
    /// <summary>A collection name.</summary>
    Collection = 71,
    /// <summary>A terminal statement failure carrying diagnostic codes and locations.</summary>
    Failure = 72,
}

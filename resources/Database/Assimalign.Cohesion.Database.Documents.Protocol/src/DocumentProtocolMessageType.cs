namespace Assimalign.Cohesion.Database.Documents.Protocol;

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

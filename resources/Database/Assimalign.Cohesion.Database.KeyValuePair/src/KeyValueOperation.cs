namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// The key-value model's operation vocabulary — the commands the engine
/// executes (see <c>docs/COMMANDS.md</c> for the text grammar they ride on).
/// </summary>
public enum KeyValueOperation : byte
{
    /// <summary>Point read of one key's visible entry.</summary>
    Get = 0,

    /// <summary>Insert or replace one key's entry, optionally conditional (compare-and-swap).</summary>
    Put,

    /// <summary>Delete one key's entry, optionally conditional (compare-and-swap).</summary>
    Delete,

    /// <summary>Visibility probe for one key.</summary>
    Exists,

    /// <summary>Ordered range scan over the key space.</summary>
    Scan,

    /// <summary>Read-only discovery of the database's key spaces and catalog metadata.</summary>
    KeySpaces,
}

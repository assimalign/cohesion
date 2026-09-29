namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// Discovers the current database's implicit key space and its catalog metadata.
/// The result contains <c>database_name</c> (string), <c>keyspace_id</c> (int64),
/// <c>entry_space_format_version</c> (int32), <c>primary_index_name</c> (string),
/// <c>index_kind</c> (string), and <c>is_unique</c> (boolean).
/// </summary>
/// <remarks>
/// Equivalent to the read-only <c>KEYSPACES</c> command. Metadata is captured
/// at command execution from the session's database catalog, including within
/// an explicit transaction. The model has no named key spaces or schema ownership.
/// </remarks>
public sealed class KeyValueKeySpacesRequest : KeyValueRequest
{
    /// <summary>
    /// Initializes a request for the current database's key-space metadata.
    /// </summary>
    public KeyValueKeySpacesRequest()
        : base(new KeyValueStatement(KeyValueOperation.KeySpaces))
    {
    }
}

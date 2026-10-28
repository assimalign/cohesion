namespace Assimalign.Cohesion.Database.Sql.Internal;

// Immutable catalog incarnation. Allocation checks reference identity, like table identities.
internal sealed class SqlCatalogSequence
{
    public SqlCatalogSequence(ulong objectId, string schema, string name, SqlSequenceOptions options)
    {
        ObjectId = objectId;
        Schema = schema;
        Name = name;
        Options = options;
    }

    public ulong ObjectId { get; }
    public string Schema { get; }
    public string Name { get; }
    public SqlSequenceOptions Options { get; }
}

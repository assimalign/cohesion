using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Immutable catalog incarnation. Allocation checks reference identity, like table identities.
[global::System.Diagnostics.DebuggerDisplay("{Schema,nq}.{Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlCatalogSequence
{
    private readonly IReadOnlyList<SqlCatalogSequence> _singleton;
    public SqlCatalogSequence(ulong objectId, string schema, string name, SqlSequenceOptions options, ulong ownerTableId = 0, string? ownerColumn = null, bool identity = false)
    {
        _singleton = Array.AsReadOnly(new[] { this });
        ObjectId = objectId;
        Schema = schema;
        Name = name;
        Options = options;
        OwnerTableId = ownerTableId; OwnerColumn = ownerColumn; IsIdentity = identity;
    }

    internal IReadOnlyList<SqlCatalogSequence> Singleton => _singleton;

    public ulong ObjectId { get; }
    public string Schema { get; }
    public string Name { get; }
    public SqlSequenceOptions Options { get; }
    public ulong OwnerTableId { get; }
    public string? OwnerColumn { get; }
    public bool IsIdentity { get; }

    private sealed class DebugView
    {
        private readonly SqlCatalogSequence _sequence;
        public DebugView(SqlCatalogSequence sequence) { _sequence = sequence; }
        public ulong ObjectId => _sequence.ObjectId;
        public string Schema => _sequence.Schema;
        public string Name => _sequence.Name;
        public SqlSequenceOptions Options => _sequence.Options;
        public ulong OwnerTableId => _sequence.OwnerTableId;
        public string? OwnerColumn => _sequence.OwnerColumn;
        public bool IsIdentity => _sequence.IsIdentity;
    }
}

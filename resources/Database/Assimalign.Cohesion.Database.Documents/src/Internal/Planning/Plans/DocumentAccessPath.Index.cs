using System.Collections.Generic;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Indexing;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentIndexPath")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentIndexPath.DebugView))]
internal sealed record DocumentIndexPath(DocumentIndexMetadata Index, DocumentSeekBound? Lower, DocumentSeekBound? Upper, IReadOnlyList<object>? EqualityPrefix = null) : DocumentAccessPath
{
    internal BTreeRangeCount? Estimate { get; init; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Internal.DocumentIndexPath _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentIndexPath value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Documents.Catalog.DocumentIndexMetadata Index => _target.Index;
        public global::Assimalign.Cohesion.Database.Documents.Internal.DocumentSeekBound? Lower => _target.Lower;
        public global::Assimalign.Cohesion.Database.Documents.Internal.DocumentSeekBound? Upper => _target.Upper;
        public global::System.Collections.Generic.IReadOnlyList<object>? EqualityPrefix => _target.EqualityPrefix;
    }
}

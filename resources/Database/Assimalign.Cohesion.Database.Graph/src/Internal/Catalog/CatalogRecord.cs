using System;

using Assimalign.Cohesion.Database.Graph.Catalog;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// One persisted catalog definition: exactly one of a label, a relationship type, a property key or
/// an index, with the kind, identity, parent and name the catalog keys its versions by.
/// </summary>
/// <param name="Label">The label definition, when the record is one.</param>
/// <param name="RelationshipType">The relationship type definition, when the record is one.</param>
/// <param name="Property">The property key definition, when the record is one.</param>
/// <param name="Index">The index definition, when the record is one.</param>
[global::System.Diagnostics.DebuggerDisplay("CatalogRecord")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.CatalogRecord.DebugView))]
internal sealed record CatalogRecord(GraphLabelMetadata? Label = null, GraphRelationshipTypeMetadata? RelationshipType = null,
    GraphPropertyKeyMetadata? Property = null, GraphIndexMetadata? Index = null)
{
    internal byte Kind => Label is not null ? (byte)1 : RelationshipType is not null ? (byte)2 : Property is not null ? (byte)3 : (byte)4;
    internal Guid Id => Label?.Id ?? RelationshipType?.Id ?? Property?.DefinitionId ?? Index!.Value.LabelId;
    internal Guid Parent => Kind <= 2 ? Guid.Empty : Id;
    internal string Name => Label?.Name ?? RelationshipType?.Name ?? Property?.Name ?? Index!.Value.Name;
    internal DatabaseObjectOwner Owner => Label?.Owner ?? RelationshipType?.Owner ?? DatabaseObjectOwner.Adhoc;
    internal string? OwningSchema => Label?.OwningSchema ?? RelationshipType?.OwningSchema;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Internal.CatalogRecord _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.CatalogRecord value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Catalog.GraphLabelMetadata? Label => _target.Label;
        public global::Assimalign.Cohesion.Database.Graph.Catalog.GraphRelationshipTypeMetadata? RelationshipType => _target.RelationshipType;
        public global::Assimalign.Cohesion.Database.Graph.Catalog.GraphPropertyKeyMetadata? Property => _target.Property;
        public global::Assimalign.Cohesion.Database.Graph.Catalog.GraphIndexMetadata? Index => _target.Index;
    }
}

using System;

using Assimalign.Cohesion.Database.Graph.Catalog;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// One persisted catalog definition: exactly one of a label, a relationship type, a property key or
/// an index or named constraint, with the kind, identity, parent and name the catalog keys its versions by.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("CatalogRecord")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.CatalogRecord.DebugView))]
internal sealed record CatalogRecord
{
    // Constraint records reuse the property/index payload slots. Ordinary record decoding keeps
    // its original allocation size; the public projections expose only the selected kind.
    private readonly GraphPropertyKeyMetadata? _property;
    private readonly GraphIndexMetadata? _index;

    public CatalogRecord(GraphLabelMetadata? label = null, GraphRelationshipTypeMetadata? relationshipType = null,
        GraphPropertyKeyMetadata? property = null, GraphIndexMetadata? index = null, GraphConstraintMetadata? constraint = null)
    {
        Label = label;
        RelationshipType = relationshipType;
        _property = constraint is { } rule
            ? new GraphPropertyKeyMetadata(rule.DefinitionId, rule.PropertyKey, rule.Type) : property;
        _index = constraint is { } named
            ? new GraphIndexMetadata(named.DefinitionId, named.Name, named.PropertyKey) : index;
    }

    public GraphLabelMetadata? Label { get; }
    public GraphRelationshipTypeMetadata? RelationshipType { get; }
    public GraphPropertyKeyMetadata? Property => _index is null ? _property : null;
    public GraphIndexMetadata? Index => _property is null ? _index : null;
    public GraphConstraintMetadata? Constraint => _property is { } property && _index is { } index
        ? new GraphConstraintMetadata(index.LabelId, index.Name, index.PropertyKey, property.Type) : null;

    internal byte Kind => Label is not null ? (byte)1 : RelationshipType is not null ? (byte)2
        : _property is not null ? _index is not null ? (byte)5 : (byte)3 : (byte)4;
    internal Guid Id => Label?.Id ?? RelationshipType?.Id ?? _property?.DefinitionId ?? _index!.Value.LabelId;
    internal Guid Parent => Kind <= 2 ? Guid.Empty : Id;
    internal string Name => Label?.Name ?? RelationshipType?.Name ?? _index?.Name ?? _property!.Value.Name;
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
        public global::Assimalign.Cohesion.Database.Graph.Catalog.GraphConstraintMetadata? Constraint => _target.Constraint;
    }
}

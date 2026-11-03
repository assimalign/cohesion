using System;

using Assimalign.Cohesion.Database.Graph.Catalog;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// One persisted catalog definition: exactly one of a label, a relationship type, a property key or
/// an index, named constraint or graph-type declaration, with the kind, identity, parent and name
/// the catalog keys its versions by.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("CatalogRecord")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.CatalogRecord.DebugView))]
internal sealed record CatalogRecord
{
    // Definitions are exclusive variants. Sharing one slot with graph-type declarations avoids
    // growing every ordinary property/index record to carry the new variants. Boxed label/type
    // structs still cost less than retaining both nullable structs in every catalog record.
    private readonly GraphPropertyKeyMetadata? _property;
    private readonly GraphIndexMetadata? _index;
    private readonly object? _definition;

    public CatalogRecord(GraphLabelMetadata? label = null, GraphRelationshipTypeMetadata? relationshipType = null,
        GraphPropertyKeyMetadata? property = null, GraphIndexMetadata? index = null, GraphConstraintMetadata? constraint = null,
        GraphTypeDeclaration? graphType = null, GraphNodeTypeDeclaration? nodeType = null, GraphEdgeTypeDeclaration? edgeType = null)
    {
        _property = constraint is { } rule
            ? new GraphPropertyKeyMetadata(rule.DefinitionId, rule.PropertyKey, rule.Type) : property;
        _index = constraint is { } named
            ? new GraphIndexMetadata(named.DefinitionId, named.Name, named.PropertyKey) : index;
        _definition = (object?)graphType ?? (object?)nodeType ?? (object?)edgeType ?? (object?)label ?? relationshipType;
    }

    public GraphLabelMetadata? Label => _definition is GraphLabelMetadata label ? label : null;
    public GraphRelationshipTypeMetadata? RelationshipType => _definition is GraphRelationshipTypeMetadata type ? type : null;
    public GraphPropertyKeyMetadata? Property => _index is null ? _property : null;
    public GraphIndexMetadata? Index => _property is null ? _index : null;
    public GraphConstraintMetadata? Constraint => _property is { } property && _index is { } index
        ? new GraphConstraintMetadata(index.LabelId, index.Name, index.PropertyKey, property.Type) : null;
    public GraphTypeDeclaration? GraphType => _definition as GraphTypeDeclaration;
    public GraphNodeTypeDeclaration? NodeType => _definition as GraphNodeTypeDeclaration;
    public GraphEdgeTypeDeclaration? EdgeType => _definition as GraphEdgeTypeDeclaration;

    internal byte Kind => _definition is GraphTypeDeclaration ? (byte)6 : _definition is GraphNodeTypeDeclaration ? (byte)7 : _definition is GraphEdgeTypeDeclaration ? (byte)8
        : Label is not null ? (byte)1 : RelationshipType is not null ? (byte)2
        : _property is not null ? _index is not null ? (byte)5 : (byte)3 : (byte)4;
    internal Guid Id => Kind >= 6 ? GraphCatalog.GraphTypeId : Label?.Id ?? RelationshipType?.Id ?? _property?.DefinitionId ?? _index!.Value.LabelId;
    internal Guid Parent => Kind is <= 2 or 6 ? Guid.Empty : Id;
    internal string Name => GraphType is not null ? "CURRENT" : NodeType?.IdentifyingLabel ?? EdgeType?.RelationshipType
        ?? Label?.Name ?? RelationshipType?.Name ?? _index?.Name ?? _property!.Value.Name;
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
        public GraphTypeDeclaration? GraphType => _target.GraphType;
        public GraphNodeTypeDeclaration? NodeType => _target.NodeType;
        public GraphEdgeTypeDeclaration? EdgeType => _target.EdgeType;
    }
}

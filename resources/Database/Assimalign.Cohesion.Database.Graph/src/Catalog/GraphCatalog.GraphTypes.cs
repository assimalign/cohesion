using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Catalog;

public sealed partial class GraphCatalog
{
    // One database-scoped type. This identity is private to kinds 6-8 and is never a token identity.
    internal static readonly Guid GraphTypeId = new("672bf6e9-3491-4306-a153-2a0f4c0370ed");
    internal Action<string>? AfterGraphTypeMutation { get; set; }

    internal GraphTypeDeclaration? GetGraphType(TransactionSnapshot snapshot, Action<long>? reserve = null)
        => LoadGraphType(snapshot, reserve, out _);

    internal GraphTypeDeclaration? LoadGraphType(TransactionSnapshot snapshot, Action<long>? reserve, out IReadOnlyList<Found> records)
    {
        var marker = Find(6, Guid.Empty, "CURRENT", snapshot, reserve);
        if (marker is null) { records = []; return null; }
        var nodes = List(7, GraphTypeId, snapshot, reserve);
        var edges = List(8, GraphTypeId, snapshot, reserve);
        records = new[] { marker.Value }.Concat(nodes).Concat(edges).ToArray();
        return GraphTypeDeclaration.Create(nodes.Select(item => item.Record.NodeType!), edges.Select(item => item.Record.EdgeType!));
    }

    internal GraphNodeTypeDeclaration? GetNodeTypeForWrite(string label, TransactionSnapshot snapshot, TransactionSnapshot latest)
    {
        // One physical read/decode per candidate pins the immutable declaration for both
        // visibility decisions and the ensuing validation. No mutable metadata cache.
        var previous = Find(7, GraphTypeId, label, snapshot, latest, out var current);
        if (previous?.Reference != current?.Reference) { throw Conflict(); }
        return previous?.Record.NodeType;
    }

    internal void EnsureGraphTypeDefinitionMutable(string name, bool relationship, TransactionSnapshot snapshot)
    {
        Guid? id = relationship ? FindRelationshipType(name, snapshot)?.Id : FindLabel(name, snapshot)?.Id;
        if (id is { } existing) { RequireMutableDefinition(existing, snapshot, "ALTER"); }
    }

    internal void EnsureNodeTypeCompatible(GraphNodeTypeDeclaration node, TransactionSnapshot snapshot, Action consume, Action<long> reserve)
    {
        if (node.Properties.Count == 0) { return; }
        if (FindLabel(node.IdentifyingLabel, snapshot) is not { } label) { return; }
        var keys = List(3, label.Id, snapshot, reserve).Select(item => item.Record.Property!.Value)
            .Select(item => (item.Name, item.Type))
            .Concat(List(5, label.Id, snapshot, reserve).Select(item => (item.Record.Constraint!.Value.PropertyKey, item.Record.Constraint.Value.Type))).ToArray();
        foreach (var property in node.Properties)
        {
            foreach (var key in keys)
            {
                consume();
                if (key.Item1 == property.Name && key.Type is { } type && type != property.Type)
                { throw ConstraintConflict(property.Name); }
            }
        }
    }

    private void EnsureGraphPropertyCompatible(Guid definition, string property, Types.DatabaseType type, TransactionSnapshot snapshot,
        Action? consume = null, Action<long>? reserve = null)
    {
        if (FindLabel(definition, snapshot) is not { } label) { return; }
        var node = Find(7, GraphTypeId, label.Name, snapshot, reserve)?.Record.NodeType;
        if (node is null) { return; }
        foreach (var item in node.Properties)
        {
            consume?.Invoke();
            if (item.Name == property && item.Type != type) { throw ConstraintConflict(property); }
        }
    }

    internal void RefuseGraphTypeUse(string name, bool relationship, TransactionSnapshot snapshot, Action consume, Action<long> reserve)
    {
        consume();
        if (Find(relationship ? (byte)8 : (byte)7, GraphTypeId, name, snapshot, reserve) is not null)
        { throw InUse(); }
        if (relationship) { return; }
        foreach (var node in List(7, GraphTypeId, snapshot, reserve))
        {
            consume();
            if (node.Record.NodeType!.ImpliedLabels.Contains(name, StringComparer.Ordinal)) { throw InUse(); }
        }
        GraphCatalogException InUse() => new("CG03003", relationship ? ErrorMessages.DropRelationshipTypeAsyncDatabase : ErrorMessages.GraphSchemaDropLabelAsyncDatabase) { IsCallerError = true };
    }

    internal async ValueTask SetGraphTypeAsync(GraphTypeDeclaration? type, IReadOnlyList<Found> previous,
        TransactionContext context, CancellationToken token)
    {
        System.Diagnostics.Debug.Assert(context.IsolationLevel == IsolationLevel.ReadCommitted);
        EnsureActive(context);
        // The sole caller asserts autocommit and writer-lock ownership, and refreshed its ReadCommitted
        // snapshot before loading these records. Reuse that load and its decode grants.
        var nodes = type?.NodeTypes.ToDictionary(item => item.IdentifyingLabel, StringComparer.Ordinal) ?? [];
        var edges = type?.EdgeTypes.ToDictionary(item => item.RelationshipType, StringComparer.Ordinal) ?? [];
        var removed = new List<Found?>();
        foreach (var old in previous)
        {
            bool unchanged = old.Record.Kind switch
            {
                6 => type is not null,
                7 => nodes.TryGetValue(old.Record.Name, out var node) && GraphTypeChanges.Same(old.Record.NodeType!, node),
                8 => edges.TryGetValue(old.Record.Name, out var edge) && GraphTypeChanges.Same(old.Record.EdgeType!, edge),
                _ => false,
            };
            if (unchanged)
            {
                if (old.Record.Kind == 7) { nodes.Remove(old.Record.Name); }
                if (old.Record.Kind == 8) { edges.Remove(old.Record.Name); }
            }
            else { removed.Add(old); }
        }
        if (removed.Count != 0)
        {
            await DeleteAsync(removed, context, token).ConfigureAwait(false);
            AfterGraphTypeMutation?.Invoke("tombstones");
        }
        if (type is null) { return; }
        if (previous.Count == 0)
        {
            await SaveAsync(new CatalogRecord(graphType: GraphTypeDeclaration.Create()), null, context, token).ConfigureAwait(false);
            AfterGraphTypeMutation?.Invoke("marker");
        }
        foreach (var node in nodes.Values)
        {
            await SaveAsync(new CatalogRecord(nodeType: node), null, context, token).ConfigureAwait(false);
            AfterGraphTypeMutation?.Invoke("node:" + node.IdentifyingLabel);
        }
        foreach (var edge in edges.Values)
        {
            await SaveAsync(new CatalogRecord(edgeType: edge), null, context, token).ConfigureAwait(false);
            AfterGraphTypeMutation?.Invoke("edge:" + edge.RelationshipType);
        }
    }
}

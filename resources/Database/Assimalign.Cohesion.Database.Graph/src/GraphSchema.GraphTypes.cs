using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph;

public sealed partial class GraphSchema
{
    /// <summary>Reads the current graph type using the session's snapshot.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The immutable open type, or null when no type is set.</returns>
    /// <exception cref="DatabaseException">The session is unavailable or its transaction refuses statements.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <exception cref="ObjectDisposedException">The database is disposed.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public ValueTask<GraphType?> GetGraphTypeAsync(CancellationToken cancellationToken = default)
        => _database.RunAsync(_session, op => new ValueTask<GraphType?>(_database.Catalog.GetGraphType(op.Context.Snapshot)), cancellationToken);

    /// <summary>Atomically replaces the current open graph type after validating existing member nodes.</summary>
    /// <param name="type">The complete replacement, including any edge declarations.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Completion after commit.</returns>
    /// <exception cref="ArgumentNullException">The type is null.</exception>
    /// <exception cref="DatabaseException">References are invalid (CG03011), existing data violates the type (CG03010), a budget is exhausted (CG05004/CG05020), or DDL is in an explicit transaction (CG04025).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The snapshot is stale (CG04007).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">Commit durability could not be confirmed.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <exception cref="ObjectDisposedException">The database is disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public ValueTask SetGraphTypeAsync(GraphType type, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ChangeGraphTypeAsync(_ => type, cancellationToken, type);
    }

    /// <summary>Adds new element types, preserving existing declarations.</summary>
    /// <param name="additions">New element types; existing identifying names are refused.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Completion after validation and commit.</returns>
    /// <exception cref="ArgumentNullException">The additions are null.</exception>
    /// <exception cref="DatabaseException">An element already exists or a reference is absent (CG03011); validation, budgets, session state or explicit-transaction policy refuses the operation.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The snapshot is stale.</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">Commit durability could not be confirmed.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <exception cref="ObjectDisposedException">The database is disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public ValueTask AddGraphTypeAsync(GraphType additions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(additions);
        return ChangeGraphTypeAsync(previous => MergeGraphTypes(previous, additions, alter: false), cancellationToken, additions);
    }

    /// <summary>Replaces the complete declarations of existing element types, preserving unmentioned types.</summary>
    /// <param name="alterations">Replacement declarations; every identifying name must already exist.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Completion after validation and commit.</returns>
    /// <exception cref="ArgumentNullException">The alterations are null.</exception>
    /// <exception cref="DatabaseException">An element or reference is absent (CG03011); validation, budgets, session state or explicit-transaction policy refuses the operation.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The snapshot is stale.</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">Commit durability could not be confirmed.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <exception cref="ObjectDisposedException">The database is disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public ValueTask AlterGraphTypeAsync(GraphType alterations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alterations);
        return ChangeGraphTypeAsync(previous => MergeGraphTypes(previous, alterations, alter: true), cancellationToken, alterations);
    }

    /// <summary>Removes the current type and its rules, retaining tokens and independent schema constraints.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Completion after commit; dropping an absent type succeeds.</returns>
    /// <exception cref="DatabaseException">Session state, budgets or explicit-transaction policy refuses the operation.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The snapshot is stale.</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">Commit durability could not be confirmed.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <exception cref="ObjectDisposedException">The database is disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public ValueTask DropGraphTypeAsync(CancellationToken cancellationToken = default)
        => ChangeGraphTypeAsync(_ => null, cancellationToken);

    /// <summary>Removes selected element types; referenced node types require dropping their edges in the same operation.</summary>
    /// <param name="nodeTypes">Identifying labels of node types to remove.</param>
    /// <param name="edgeTypes">Relationship types of edge types to remove.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Completion after commit. Removing the last elements leaves an empty type set; empty name lists preserve the current state.</returns>
    /// <exception cref="ArgumentException">A collection is null or contains a blank or repeated name.</exception>
    /// <exception cref="DatabaseException">An element is absent or an edge would have a dangling reference (CG03011); validation, budgets, session state or explicit-transaction policy refuses the operation.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The snapshot is stale.</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">Commit durability could not be confirmed.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <exception cref="ObjectDisposedException">The database is disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public ValueTask DropGraphTypeAsync(IReadOnlyList<string> nodeTypes, IReadOnlyList<string> edgeTypes,
        CancellationToken cancellationToken = default)
    {
        var nodes = CopyNames(nodeTypes);
        var edges = CopyNames(edgeTypes);
        return ChangeGraphTypeAsync(previous =>
        {
            if (nodes.Count == 0 && edges.Count == 0) { return previous; }
            foreach (string node in nodes)
            { if (previous?.NodeTypes.Any(item => item.IdentifyingLabel == node) != true) { throw InvalidGraphTypeChange("DROP", node); } }
            foreach (string edge in edges)
            { if (previous?.EdgeTypes.Any(item => item.RelationshipType == edge) != true) { throw InvalidGraphTypeChange("DROP", edge); } }
            return GraphType.Create(previous?.NodeTypes.Where(item => !nodes.Contains(item.IdentifyingLabel)),
                previous?.EdgeTypes.Where(item => !edges.Contains(item.RelationshipType)));
        }, cancellationToken);
    }

    private static HashSet<string> CopyNames(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!result.Add(name)) { throw new ArgumentException(ErrorMessages.GraphTypeDuplicate, nameof(names)); }
        }
        return result;
    }

    private static GraphType? MergeGraphTypes(GraphType? previous, GraphType changes, bool alter)
    {
        if (changes.NodeTypes.Count == 0 && changes.EdgeTypes.Count == 0) { return previous; }
        var nodes = (previous?.NodeTypes ?? []).ToDictionary(item => item.IdentifyingLabel, StringComparer.Ordinal);
        var edges = (previous?.EdgeTypes ?? []).ToDictionary(item => item.RelationshipType, StringComparer.Ordinal);
        foreach (var node in changes.NodeTypes)
        {
            if (nodes.ContainsKey(node.IdentifyingLabel) != alter) { throw InvalidGraphTypeChange(alter ? "ALTER" : "ADD", node.IdentifyingLabel); }
            nodes[node.IdentifyingLabel] = node;
        }
        foreach (var edge in changes.EdgeTypes)
        {
            if (edges.ContainsKey(edge.RelationshipType) != alter) { throw InvalidGraphTypeChange(alter ? "ALTER" : "ADD", edge.RelationshipType); }
            edges[edge.RelationshipType] = edge;
        }
        return GraphType.Create(nodes.Values, edges.Values);
    }

    private static DatabaseException InvalidGraphTypeChange(string operation, string name)
    {
        // A missing DROP name is caller input, with no catalog-record bound. Keep diagnostics
        // bounded and preserve a surrogate pair at the truncation boundary.
        int length = Math.Min(name.Length, 256);
        if (length < name.Length && char.IsHighSurrogate(name[length - 1]) && char.IsLowSurrogate(name[length])) { length--; }
        return DatabaseException.CreateGraphFailure("CG03011", string.Format(CultureInfo.CurrentCulture,
            ErrorMessages.GraphTypeChangeInvalid, operation, name[..length]));
    }

    private ValueTask ChangeGraphTypeAsync(Func<GraphType?, GraphType?> change, CancellationToken token, GraphType? input = null)
        => WriteAsync(async op =>
        {
            using var memory = new GraphMemoryOwner(op.QueryBudget!.Memory);
            op.QueryBudget.Consume();
            memory.Add(512); // Current-type publication and its singleton catalog route.
            // Admit fragment capacity before ADD/ALTER allocate their dictionaries and copied
            // result arrays. Existing declarations are covered by the catalog decode grant.
            if (input is not null)
            {
                foreach (var node in input.NodeTypes)
                {
                    token.ThrowIfCancellationRequested();
                    op.QueryBudget.Consume();
                    memory.Add(512);
                    GraphCatalogCodec.GetEncodedSize(new CatalogRecord(nodeType: node));
                }
                foreach (var edge in input.EdgeTypes)
                {
                    token.ThrowIfCancellationRequested();
                    op.QueryBudget.Consume();
                    memory.Add(512);
                    GraphCatalogCodec.GetEncodedSize(new CatalogRecord(edgeType: edge));
                }
            }
            var previous = _database.Catalog.GetGraphType(op.Context.Snapshot, memory.Add);
            var type = change(previous);
            if (ReferenceEquals(type, previous)) { return; }
            if (type is not null)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in type.NodeTypes)
                {
                    token.ThrowIfCancellationRequested();
                    op.QueryBudget.Consume();
                    GraphCatalogCodec.GetEncodedSize(new CatalogRecord(nodeType: node));
                    memory.Add(512L + node.IdentifyingLabel.Length * 2L + node.ImpliedLabels.Sum(label => 48L + label.Length * 2L)
                        + node.Properties.Sum(property => 128L + property.Name.Length * 2L));
                    names.Add(node.IdentifyingLabel);
                }
                foreach (var edge in type.EdgeTypes)
                {
                    token.ThrowIfCancellationRequested();
                    op.QueryBudget.Consume();
                    GraphCatalogCodec.GetEncodedSize(new CatalogRecord(edgeType: edge));
                    memory.Add(512L + (edge.RelationshipType.Length + edge.SourceNodeType.Length + edge.TargetNodeType.Length) * 2L
                        + edge.Properties.Sum(property => 128L + property.Name.Length * 2L));
                    if (!names.Contains(edge.SourceNodeType) || !names.Contains(edge.TargetNodeType))
                    { throw DatabaseException.CreateGraphFailure("CG03011", string.Format(CultureInfo.CurrentCulture, ErrorMessages.GraphTypeReferenceMissing, edge.RelationshipType)); }
                }
                var latest = _database.LatestSnapshot(op.Context);
                foreach (var nodeType in type.NodeTypes)
                {
                    using var cursorMemory = new GraphMemoryOwner(op.QueryBudget.Memory);
                    foreach (var node in _database.Store.StreamLabels([nodeType.IdentifyingLabel], latest, token, op.QueryBudget.Consume, cursorMemory.Add))
                    {
                        using var nodeMemory = new GraphMemoryOwner(op.QueryBudget.Memory);
                        nodeMemory.RetainValue(GraphDatabase.Materialize(node), token);
                        GraphDatabase.ValidateNodeType(nodeType, node.Labels, node.Properties, false, existingData: true);
                    }
                }
                // Discover tokens in the same logical transaction, independently of type enforcement.
                foreach (var node in type.NodeTypes)
                {
                    op.QueryBudget.Consume();
                    if (_database.Catalog.FindLabel(node.IdentifyingLabel, op.Context.Snapshot) is null) { memory.Add(1024); }
                    await EnsureDefinitionAsync(op, node.IdentifyingLabel, false, token).ConfigureAwait(false);
                    _database.Catalog.AfterGraphTypeMutation?.Invoke("label:" + node.IdentifyingLabel);
                    foreach (string label in node.ImpliedLabels)
                    {
                        op.QueryBudget.Consume();
                        if (_database.Catalog.FindLabel(label, op.Context.Snapshot) is null) { memory.Add(1024); }
                        await EnsureDefinitionAsync(op, label, false, token).ConfigureAwait(false);
                        _database.Catalog.AfterGraphTypeMutation?.Invoke("label:" + label);
                    }
                    foreach (var property in node.Properties)
                    {
                        op.QueryBudget.Consume();
                        if (!_database.Catalog.HasPropertyKey(property.Name, op.Context.Snapshot)) { memory.Add(1024); }
                        await _database.Catalog.DiscoverPropertyKeyAsync(property.Name, op.Context, token).ConfigureAwait(false);
                        _database.Catalog.AfterGraphTypeMutation?.Invoke("property:" + property.Name);
                    }
                }
                foreach (var edge in type.EdgeTypes)
                {
                    op.QueryBudget.Consume();
                    if (_database.Catalog.FindRelationshipType(edge.RelationshipType, op.Context.Snapshot) is null) { memory.Add(1024); }
                    await EnsureDefinitionAsync(op, edge.RelationshipType, true, token).ConfigureAwait(false);
                    _database.Catalog.AfterGraphTypeMutation?.Invoke("type:" + edge.RelationshipType);
                    foreach (var property in edge.Properties)
                    {
                        op.QueryBudget.Consume();
                        if (!_database.Catalog.HasPropertyKey(property.Name, op.Context.Snapshot)) { memory.Add(1024); }
                        await _database.Catalog.DiscoverPropertyKeyAsync(property.Name, op.Context, token).ConfigureAwait(false);
                        _database.Catalog.AfterGraphTypeMutation?.Invoke("property:" + property.Name);
                    }
                }
            }
            await _database.Catalog.SetGraphTypeAsync(type, op.Context, token, memory.Add).ConfigureAwait(false);
        }, token);
}

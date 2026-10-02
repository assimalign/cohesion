using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage;

/// <summary>Transactional graph records, adjacency, and exact node-property indexes.</summary>
public interface IGraphStore
{
    /// <summary>Creates a node and maintains its property indexes.</summary>
    /// <param name="labels">Node labels.</param><param name="properties">Scalar properties.</param><param name="context">Owning transaction.</param><param name="cancellationToken">Cancellation token.</param><returns>The created node.</returns>
    /// <exception cref="System.ArgumentException">A label or property key is null or whitespace, or a property value is not a supported scalar.</exception>
    /// <exception cref="GraphElementTooLargeException">The labels and properties exceed one graph record, or an indexed property value exceeds the index key.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended while waiting for its writer lock.</exception>
    ValueTask<StoredGraphNode> CreateNodeAsync(IReadOnlyList<string> labels, IReadOnlyDictionary<string, object?> properties, ITransactionContext context, CancellationToken cancellationToken = default);
    /// <summary>Finds a node visible to a snapshot.</summary>
    /// <param name="id">Node identity.</param><param name="snapshot">Visibility snapshot.</param><returns>The visible node or null.</returns>
    StoredGraphNode? FindNode(ulong id, TransactionSnapshot snapshot);
    /// <summary>Enumerates visible nodes, optionally restricted by label.</summary>
    /// <param name="label">Optional label.</param><param name="snapshot">Visibility snapshot.</param><returns>The visible nodes.</returns>
    IReadOnlyList<StoredGraphNode> GetNodes(string? label, TransactionSnapshot snapshot);
    /// <summary>Atomically creates a relationship and both endpoint adjacency entries.</summary>
    /// <param name="sourceId">Source node.</param><param name="targetId">Target node.</param><param name="type">Relationship type.</param><param name="properties">Scalar properties.</param><param name="context">Owning transaction.</param><param name="cancellationToken">Cancellation token.</param><returns>The created relationship.</returns>
    /// <exception cref="System.InvalidOperationException">An endpoint does not exist in the caller's snapshot.</exception>
    /// <exception cref="TransactionAbortedException">An endpoint changed after the transaction snapshot.</exception>
    /// <exception cref="System.ArgumentException">The type or a property key is null or whitespace, or a property value is not a supported scalar.</exception>
    /// <exception cref="GraphElementTooLargeException">The type and properties exceed one graph record.</exception>
    ValueTask<StoredGraphRelationship> CreateRelationshipAsync(ulong sourceId, ulong targetId, string type, IReadOnlyDictionary<string, object?> properties, ITransactionContext context, CancellationToken cancellationToken = default);
    /// <summary>Finds a visible relationship.</summary>
    /// <param name="id">Relationship identity.</param><param name="snapshot">Visibility snapshot.</param><returns>The visible relationship or null.</returns>
    StoredGraphRelationship? FindRelationship(ulong id, TransactionSnapshot snapshot);
    /// <summary>Seeks the shared B+Tree for a node's incident relationships; self-loops appear once.</summary>
    /// <param name="nodeId">Node identity.</param><param name="snapshot">Visibility snapshot.</param><param name="cancellationToken">Cancellation token.</param><returns>Visible incident relationships ordered by identity.</returns>
    ValueTask<IReadOnlyList<StoredGraphRelationship>> GetIncidentAsync(ulong nodeId, TransactionSnapshot snapshot, CancellationToken cancellationToken = default);
    /// <summary>Deletes a node, atomically cascading incident relationships when requested.</summary>
    /// <param name="id">Node identity.</param><param name="detach">Whether to cascade relationships.</param><param name="context">Owning transaction.</param><param name="cancellationToken">Cancellation token.</param><returns>A task representing deletion.</returns>
    /// <exception cref="System.InvalidOperationException">A connected node is deleted without detach.</exception>
    /// <exception cref="TransactionAbortedException">The node or its incident relationships changed after the snapshot.</exception>
    ValueTask DeleteNodeAsync(ulong id, bool detach, ITransactionContext context, CancellationToken cancellationToken = default);
    /// <summary>Deletes a relationship and its adjacency entries atomically.</summary>
    /// <param name="id">Relationship identity.</param><param name="context">Owning transaction.</param><param name="cancellationToken">Cancellation token.</param><returns>A task representing deletion.</returns>
    /// <exception cref="TransactionAbortedException">The relationship changed after the transaction snapshot.</exception>
    ValueTask DeleteRelationshipAsync(ulong id, ITransactionContext context, CancellationToken cancellationToken = default);
    /// <summary>Builds a transactional B+Tree for a node label and property.</summary>
    /// <param name="label">Node label.</param><param name="propertyKey">Property name.</param><param name="context">Owning transaction.</param><param name="cancellationToken">Cancellation token.</param><returns>A task representing index creation.</returns>
    /// <exception cref="System.InvalidOperationException">An index already exists for this label/property pair.</exception>
    /// <exception cref="System.ArgumentException">The label or property name is null or whitespace.</exception>
    /// <exception cref="GraphElementTooLargeException">The names exceed one graph record, or an existing node's value exceeds the index key.</exception>
    ValueTask CreateIndexAsync(string label, string propertyKey, ITransactionContext context, CancellationToken cancellationToken = default);
    /// <summary>Drops a node-property index definition; older snapshots retain its tree.</summary>
    /// <param name="label">Node label.</param><param name="propertyKey">Property name.</param><param name="context">Owning transaction.</param><param name="cancellationToken">Cancellation token.</param><returns>A task representing index deletion.</returns>
    /// <exception cref="System.InvalidOperationException">No visible index exists for this label/property pair.</exception>
    /// <exception cref="TransactionAbortedException">The index changed after the transaction snapshot.</exception>
    ValueTask DropIndexAsync(string label, string propertyKey, ITransactionContext context, CancellationToken cancellationToken = default);
    /// <summary>Tests whether an exact property index is visible.</summary>
    /// <param name="label">Node label.</param><param name="propertyKey">Property name.</param><param name="snapshot">Visibility snapshot.</param><returns>True when an index is visible.</returns>
    bool HasIndex(string label, string propertyKey, TransactionSnapshot snapshot);
    /// <summary>
    /// Lists every exact property index visible to a snapshot in one pass over the index
    /// definitions, so a planner can match many labels and property keys without a lookup per pair.
    /// </summary>
    /// <param name="snapshot">Visibility snapshot.</param><returns>The visible indexes, each label/property pair once.</returns>
    IReadOnlyList<StoredGraphIndex> GetIndexes(TransactionSnapshot snapshot);
    /// <summary>Seeks an exact scalar property value through its B+Tree.</summary>
    /// <param name="label">Node label.</param><param name="propertyKey">Property name.</param><param name="value">Scalar value.</param><param name="snapshot">Visibility snapshot.</param><param name="cancellationToken">Cancellation token.</param><returns>The matching visible nodes.</returns>
    /// <exception cref="System.InvalidOperationException">No visible index exists for this label/property pair.</exception>
    /// <remarks>A value too long for the index key matches nothing: no write can store one.</remarks>
    /// <exception cref="System.ArgumentException">The scalar bound is not a supported scalar.</exception>
    ValueTask<IReadOnlyList<StoredGraphNode>> SearchIndexAsync(string label, string propertyKey, object? value, TransactionSnapshot snapshot, CancellationToken cancellationToken = default);
    /// <summary>Scrubs unproven index writers after coordinator record recovery and before checkpoint.</summary>
    /// <param name="writers">Aborted or uncommitted writers.</param><param name="cancellationToken">Cancellation token.</param><returns>A task representing recovery.</returns>
    ValueTask RecoverIndexesAsync(IReadOnlySet<TransactionSequence> writers, CancellationToken cancellationToken = default);
}

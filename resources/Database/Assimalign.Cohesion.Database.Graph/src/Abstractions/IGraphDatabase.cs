using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// Represents a property-graph database: labeled nodes connected by typed,
/// directed relationships, both carrying properties.
/// </summary>
public interface IGraphDatabase : IDatabase
{
    /// <summary>
    /// Creates a node with the specified labels and properties.
    /// </summary>
    /// <param name="session">The session the write executes in.</param>
    /// <param name="labels">The labels applied to the node.</param>
    /// <param name="properties">The node's properties, or null for none.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created node.</returns>
    ValueTask<GraphNode> CreateNodeAsync(IDatabaseSession session, IReadOnlyList<string> labels, IReadOnlyDictionary<string, object?>? properties = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a node by identity.
    /// </summary>
    /// <param name="session">The session the read executes in.</param>
    /// <param name="id">The node identity.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The node, or null when no visible node has the identity.</returns>
    ValueTask<GraphNode?> GetNodeAsync(IDatabaseSession session, GraphNodeId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a node and its attached relationships.
    /// </summary>
    /// <param name="session">The session the delete executes in.</param>
    /// <param name="id">The node identity.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when a node was deleted; false when none was visible.</returns>
    ValueTask<bool> DeleteNodeAsync(IDatabaseSession session, GraphNodeId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a typed, directed relationship between two nodes.
    /// </summary>
    /// <param name="session">The session the write executes in.</param>
    /// <param name="from">The origin node.</param>
    /// <param name="to">The target node.</param>
    /// <param name="type">The relationship type.</param>
    /// <param name="properties">The relationship's properties, or null for none.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created relationship.</returns>
    /// <exception cref="DatabaseException">Thrown when either endpoint node does not exist.</exception>
    ValueTask<GraphRelationship> CreateRelationshipAsync(IDatabaseSession session, GraphNodeId from, GraphNodeId to, string type, IReadOnlyDictionary<string, object?>? properties = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a relationship by identity.
    /// </summary>
    /// <param name="session">The session the delete executes in.</param>
    /// <param name="id">The relationship identity.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when a relationship was deleted; false when none was visible.</returns>
    ValueTask<bool> DeleteRelationshipAsync(IDatabaseSession session, GraphRelationshipId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Traverses the graph from a starting node, streaming visited nodes.
    /// </summary>
    /// <param name="session">The session the traversal executes in.</param>
    /// <param name="traversal">The traversal specification.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of visited nodes, excluding the start node.</returns>
    IAsyncEnumerable<GraphNode> TraverseAsync(IDatabaseSession session, GraphTraversal traversal, CancellationToken cancellationToken = default);
}

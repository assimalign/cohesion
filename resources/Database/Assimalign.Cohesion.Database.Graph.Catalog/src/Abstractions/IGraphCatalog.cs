using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Catalog;

/// <summary>The durable, snapshot-visible definitions of one logical graph database.</summary>
/// <remarks>Mutations join the supplied transaction without committing it. The caller holds the
/// database's definition lock and handles any graph data affected by a definition change.
/// Existing schema-owned definitions refuse all alterations and drops. Initial marked
/// definitions support the ownership enforcement seam; compiled provisioning is separate.</remarks>
public interface IGraphCatalog
{
    /// <summary>Finds a visible label by its ordinal name.</summary>
    /// <param name="name">The case-sensitive name.</param>
    /// <param name="snapshot">The reader's snapshot.</param>
    /// <returns>The definition, or null.</returns>
    GraphLabelMetadata? FindLabel(string name, TransactionSnapshot snapshot);

    /// <summary>Lists visible label definitions in ordinal name order.</summary>
    /// <param name="snapshot">The reader's snapshot.</param>
    /// <returns>The visible definitions.</returns>
    IReadOnlyList<GraphLabelMetadata> GetLabels(TransactionSnapshot snapshot);

    /// <summary>Creates or alters a label definition in the supplied transaction.</summary>
    /// <param name="label">The complete definition.</param>
    /// <param name="context">The active logical transaction.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="ArgumentException">The definition is invalid.</exception>
    /// <exception cref="GraphCatalogException">The identity or name conflicts with an existing definition.</exception>
    /// <exception cref="DatabaseObjectLockedException">The existing definition is owned by a schema.</exception>
    ValueTask SaveLabelAsync(GraphLabelMetadata label, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Drops a label and its property/index metadata in the supplied transaction.</summary>
    /// <param name="id">The stable definition identity.</param>
    /// <param name="context">The active logical transaction.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The definition is owned by a schema.</exception>
    ValueTask DeleteLabelAsync(Guid id, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Finds a visible relationshipType by its ordinal name.</summary>
    /// <param name="name">The case-sensitive name.</param>
    /// <param name="snapshot">The reader's snapshot.</param>
    /// <returns>The definition, or null.</returns>
    GraphRelationshipTypeMetadata? FindRelationshipType(string name, TransactionSnapshot snapshot);

    /// <summary>Lists visible relationshipType definitions in ordinal name order.</summary>
    /// <param name="snapshot">The reader's snapshot.</param>
    /// <returns>The visible definitions.</returns>
    IReadOnlyList<GraphRelationshipTypeMetadata> GetRelationshipTypes(TransactionSnapshot snapshot);

    /// <summary>Creates or alters a relationshipType definition in the supplied transaction.</summary>
    /// <param name="relationshipType">The complete definition.</param>
    /// <param name="context">The active logical transaction.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="ArgumentException">The definition is invalid.</exception>
    /// <exception cref="GraphCatalogException">The identity or name conflicts with an existing definition.</exception>
    /// <exception cref="DatabaseObjectLockedException">The existing definition is owned by a schema.</exception>
    ValueTask SaveRelationshipTypeAsync(GraphRelationshipTypeMetadata relationshipType, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Drops a relationshipType and its property/index metadata in the supplied transaction.</summary>
    /// <param name="id">The stable definition identity.</param>
    /// <param name="context">The active logical transaction.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The definition is owned by a schema.</exception>
    ValueTask DeleteRelationshipTypeAsync(Guid id, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Lists a definition's property keys in ordinal name order.</summary>
    /// <param name="definitionId">The label or relationship-type identity.</param>
    /// <param name="snapshot">The visibility snapshot.</param>
    /// <returns>The visible property definitions.</returns>
    IReadOnlyList<GraphPropertyKeyMetadata> GetPropertyKeys(Guid definitionId, TransactionSnapshot snapshot);

    /// <summary>Creates or alters property metadata on an existing mutable definition.</summary>
    /// <param name="property">The complete property definition.</param>
    /// <param name="context">The active logical transaction.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="ArgumentException">The property metadata is invalid.</exception>
    /// <exception cref="GraphCatalogException">The owning definition is absent.</exception>
    /// <exception cref="DatabaseObjectLockedException">The owning definition is schema-owned.</exception>
    ValueTask SavePropertyKeyAsync(GraphPropertyKeyMetadata property, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Drops property metadata from an existing mutable definition.</summary>
    /// <param name="definitionId">The label or relationship-type identity.</param>
    /// <param name="name">The property key.</param>
    /// <param name="context">The active logical transaction.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="GraphCatalogException">The owning definition is absent.</exception>
    /// <exception cref="DatabaseObjectLockedException">The owning definition is schema-owned.</exception>
    ValueTask DeletePropertyKeyAsync(Guid definitionId, string name, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Lists index definitions belonging to a label in ordinal name order.</summary>
    /// <param name="labelId">The label identity.</param>
    /// <param name="snapshot">The visibility snapshot.</param>
    /// <returns>The visible index definitions.</returns>
    IReadOnlyList<GraphIndexMetadata> GetIndexes(Guid labelId, TransactionSnapshot snapshot);

    /// <summary>Publishes named index metadata alongside the physical graph-store index.</summary>
    /// <param name="index">The complete index definition.</param>
    /// <param name="context">The active transaction that also owns the physical index change.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="ArgumentException">The index metadata is invalid.</exception>
    /// <exception cref="GraphCatalogException">The label is absent or the name identifies another property.</exception>
    /// <exception cref="DatabaseObjectLockedException">The label is schema-owned.</exception>
    ValueTask SaveIndexAsync(GraphIndexMetadata index, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Tombstones named index metadata alongside the physical graph-store index.</summary>
    /// <param name="labelId">The label identity.</param>
    /// <param name="name">The index name.</param>
    /// <param name="context">The active transaction that also owns the physical index change.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="GraphCatalogException">The label is absent.</exception>
    /// <exception cref="DatabaseObjectLockedException">The label is schema-owned.</exception>
    ValueTask DeleteIndexAsync(Guid labelId, string name, ITransactionContext context, CancellationToken cancellationToken = default);
}


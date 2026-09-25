using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Graph.Catalog;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Database-scoped label, relationship-type and index management bound to one session.</summary>
public interface IGraphSchema
{
    /// <summary>Lists visible labels.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The visible label definitions.</returns>
    ValueTask<IReadOnlyList<GraphLabelMetadata>> GetLabelsAsync(CancellationToken cancellationToken = default);
    /// <summary>Lists visible relationship types.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The visible relationship type definitions.</returns>
    ValueTask<IReadOnlyList<GraphRelationshipTypeMetadata>> GetRelationshipTypesAsync(CancellationToken cancellationToken = default);
    /// <summary>Creates or alters a label definition.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The existing label is schema owned.</exception>
    ValueTask SaveLabelAsync(GraphLabelMetadata definition, CancellationToken cancellationToken = default);
    /// <summary>Creates or alters a relationship type definition.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The existing type is schema owned.</exception>
    ValueTask SaveRelationshipTypeAsync(GraphRelationshipTypeMetadata definition, CancellationToken cancellationToken = default);
    /// <summary>Drops an unused label definition.</summary>
    /// <param name="name">The label name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The label is schema owned.</exception>
    /// <exception cref="DatabaseException">The label is missing or in use.</exception>
    ValueTask DropLabelAsync(string name, CancellationToken cancellationToken = default);
    /// <summary>Drops an unused relationship type definition.</summary>
    /// <param name="name">The type name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The type is schema owned.</exception>
    /// <exception cref="DatabaseException">The type is missing or in use.</exception>
    ValueTask DropRelationshipTypeAsync(string name, CancellationToken cancellationToken = default);
    /// <summary>Creates or alters property-key metadata.</summary>
    /// <param name="definition">The property definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The owning definition is schema owned.</exception>
    ValueTask SavePropertyKeyAsync(GraphPropertyKeyMetadata definition, CancellationToken cancellationToken = default);
    /// <summary>Lists property metadata for a label or relationship type.</summary>
    /// <param name="definitionId">The label or relationship type identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The visible property definitions.</returns>
    ValueTask<IReadOnlyList<GraphPropertyKeyMetadata>> GetPropertyKeysAsync(System.Guid definitionId, CancellationToken cancellationToken = default);
    /// <summary>Builds a B+Tree index over a node property, including existing nodes.</summary>
    /// <param name="label">The indexed label.</param>
    /// <param name="name">The index name.</param>
    /// <param name="propertyKey">The property name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of index creation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The label is schema owned.</exception>
    ValueTask CreateIndexAsync(string label, string name, string propertyKey, CancellationToken cancellationToken = default);
    /// <summary>Lists a label's property indexes.</summary>
    /// <param name="label">The label name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The visible indexes.</returns>
    ValueTask<IReadOnlyList<GraphIndexMetadata>> GetIndexesAsync(string label, CancellationToken cancellationToken = default);
}

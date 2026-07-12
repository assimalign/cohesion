using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Exposes the physical registrations of an index manager's live indexes, so model
/// catalogs can persist them (and re-attach them on open through
/// <see cref="BTreeIndexManagerOptions.ExistingIndexes"/>).
/// </summary>
/// <remarks>
/// Index-directory persistence deliberately belongs to the model catalog, not this
/// project: the catalog owns schema metadata and its transactional DDL apply, and
/// the index manager stays a purely physical component. Root page identifiers
/// change when a root splits — catalogs re-export at their persistence points.
/// </remarks>
public interface IIndexRegistry
{
    /// <summary>
    /// Gets the current registrations of every live index.
    /// </summary>
    /// <returns>The registrations, one per index.</returns>
    IReadOnlyList<BTreeIndexRegistration> ExportRegistrations();
}

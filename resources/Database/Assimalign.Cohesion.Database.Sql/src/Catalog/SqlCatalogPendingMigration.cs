using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>A durable migration intent, retained until the complete target schema is recorded.</summary>
public sealed class SqlCatalogPendingMigration
{
    /// <summary>Initializes a migration intent in execution order, after its inline steps.</summary>
    /// <param name="contentHash">The target schema's content hash.</param>
    /// <param name="canonicalDocument">The target schema's canonical document.</param>
    /// <param name="steps">The deferred steps, in execution order.</param>
    /// <param name="stepCount">The total number of inline and deferred steps.</param>
    /// <exception cref="ArgumentException">An identity is blank or the step positions are inconsistent.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="steps"/> is null.</exception>
    public SqlCatalogPendingMigration(string contentHash, string canonicalDocument,
        IReadOnlyList<SqlCatalogMigrationStep> steps, int stepCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDocument);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0 || stepCount < steps.Count)
        {
            throw new ArgumentException(ErrorMessages.SqlCatalogPendingMigrationArgument, nameof(steps));
        }

        int previous = 0;
        foreach (var step in steps)
        {
            if (step is null || step.Position <= previous || step.Position > stepCount)
            {
                throw new ArgumentException(ErrorMessages.SqlCatalogPendingMigrationArgument2, nameof(steps));
            }
            previous = step.Position;
        }

        ContentHash = contentHash;
        CanonicalDocument = canonicalDocument;
        Steps = Array.AsReadOnly(steps.ToArray());
        StepCount = stepCount;
    }

    /// <summary>Gets the target schema's content hash.</summary>
    public string ContentHash { get; }
    /// <summary>Gets the target schema's canonical document.</summary>
    public string CanonicalDocument { get; }
    /// <summary>Gets the durable deferred step order.</summary>
    public IReadOnlyList<SqlCatalogMigrationStep> Steps { get; }
    /// <summary>Gets the total number of inline and deferred steps.</summary>
    public int StepCount { get; }
}

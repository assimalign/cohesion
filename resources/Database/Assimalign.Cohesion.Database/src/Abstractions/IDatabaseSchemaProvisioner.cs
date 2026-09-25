using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>Applies a compiled schema to a logical database.</summary>
public interface IDatabaseSchemaProvisioner
{
    /// <summary>Diffs and applies the desired compiled schema.</summary>
    /// <param name="schema">The desired schema.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The migration result.</returns>
    ValueTask<SchemaMigrationResult> ApplySchemaAsync(
        CompiledSchema schema,
        CancellationToken cancellationToken = default);
}


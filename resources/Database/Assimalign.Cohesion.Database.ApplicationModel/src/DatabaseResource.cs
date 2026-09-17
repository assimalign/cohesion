using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Database.ApplicationModel;

/// <summary>
/// Represents a manifest-backed Cohesion database in an application graph.
/// </summary>
/// <remarks>
/// The resource contains only platform-neutral orchestration facts. Its manifest is
/// produced by an enabled database executable, and its realization plan contains no
/// platform-specific objects or runtime database dependencies.
/// </remarks>
public sealed class DatabaseResource : PlannedResource
{
    private const string DatabasePlannerName = "Database planner";

    /// <summary>
    /// Initializes a new <see cref="DatabaseResource"/> from a build-produced manifest.
    /// </summary>
    /// <param name="manifest">The database resource manifest.</param>
    /// <param name="options">
    /// Optional deployer-owned replica and storage overrides; manifest values are used
    /// when no override is supplied.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="manifest"/> is <see langword="null"/>.
    /// </exception>
    public DatabaseResource(
        ResourceManifest manifest,
        DatabaseResourceOptions? options = null)
        : base(manifest, options ?? new DatabaseResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => DatabasePlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return DatabasePlanner.CreatePlan(context);
    }
}

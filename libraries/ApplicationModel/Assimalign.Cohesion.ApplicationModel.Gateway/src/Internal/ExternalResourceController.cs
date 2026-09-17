using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Resolves external resources without asking a platform controller to realize them.
/// </summary>
internal sealed class ExternalResourceController : IApplicationResourceController
{
    internal const string PlanHint = "cohesion.external";

    private readonly Func<IApplicationModel, IControlPlaneClient?> _controlPlaneClient;

    public ExternalResourceController(Func<IApplicationModel, IControlPlaneClient?> controlPlaneClient)
    {
        _controlPlaneClient = controlPlaneClient ?? throw new ArgumentNullException(nameof(controlPlaneClient));
    }

    public bool CanRealize(ResourcePlan plan, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Hints.TryGetValue(PlanHint, out string? value)
            && string.Equals(value, bool.TrueString, StringComparison.OrdinalIgnoreCase))
        {
            reason = null;
            return true;
        }

        reason = null;
        return false;
    }

    public async Task ReconcileAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Resource is not IExternalResource external)
        {
            throw new InvalidOperationException(
                $"Resource '{context.Resource.Name}' carries the '{PlanHint}' plan hint but does not " +
                $"implement {nameof(IExternalResource)}.");
        }

        ExternalResourceResolution resolution = await external.Resolver
            .ResolveAsync(
                new ExternalResourceResolutionContext(
                    external.Declaration,
                    _controlPlaneClient(context.Model)),
                cancellationToken)
            .ConfigureAwait(false);

        if (resolution is null)
        {
            throw new InvalidOperationException(
                $"The external resolver returned null for resource '{external.Declaration.Name}'.");
        }

        if (!resolution.Resolved)
        {
            ResourceLifecycle state = external.Declaration.Optional
                ? ResourceLifecycle.Skipped
                : ResourceLifecycle.Starting;
            context.State.SetState(
                external.Id,
                state,
                resolution.Detail ?? $"External '{external.Declaration.Name}' is unresolved.");
            return;
        }

        IReadOnlyList<string> missingEndpoints = FindMissingEndpoints(
            external.Declaration.ReferencedEndpoints,
            resolution.Endpoints);
        if (missingEndpoints.Count != 0)
        {
            context.State.SetState(
                external.Id,
                ResourceLifecycle.Failed,
                CreateMissingEndpointDetail(
                    external.Declaration,
                    resolution,
                    missingEndpoints));
            return;
        }

        string? detail = resolution.Detail;
        if (HasManifestDrift(external.Declaration, resolution))
        {
            string drift = new ManifestDrift(
                external.Declaration.Name,
                external.Declaration.ManifestHash!,
                resolution.ManifestHash!,
                ApplicationExportDocument.CurrentSchemaVersion,
                resolution.SchemaVersion ?? ApplicationExportDocument.CurrentSchemaVersion)
                .Message;
            detail = string.IsNullOrWhiteSpace(detail) ? drift : $"{detail} {drift}";
        }

        context.State.SetState(
            external.Id,
            ResourceLifecycle.Running,
            detail,
            resolution.Endpoints);
    }

    public Task StopAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        context.State.SetState(context.Resource.Id, ResourceLifecycle.Stopped);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        context.State.SetState(context.Resource.Id, ResourceLifecycle.Stopped);
        return Task.CompletedTask;
    }

    private static IReadOnlyList<string> FindMissingEndpoints(
        IReadOnlyList<string> referencedEndpoints,
        IReadOnlyList<ResourceEndpoint> resolvedEndpoints)
    {
        var available = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < resolvedEndpoints.Count; index++)
        {
            available.Add(resolvedEndpoints[index].Name);
        }

        var missing = new List<string>();
        var recorded = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < referencedEndpoints.Count; index++)
        {
            string endpoint = referencedEndpoints[index];
            if (!available.Contains(endpoint) && recorded.Add(endpoint))
            {
                missing.Add(endpoint);
            }
        }

        return missing;
    }

    private static bool HasManifestDrift(
        ExternalResourceDeclaration declaration,
        ExternalResourceResolution resolution) =>
        declaration.ManifestHash is not null
        && resolution.ManifestHash is not null
        && (!string.Equals(
                declaration.ManifestHash,
                resolution.ManifestHash,
                StringComparison.OrdinalIgnoreCase)
            || (resolution.SchemaVersion is int schemaVersion
                && schemaVersion != ApplicationExportDocument.CurrentSchemaVersion));

    private static string CreateMissingEndpointDetail(
        ExternalResourceDeclaration declaration,
        ExternalResourceResolution resolution,
        IReadOnlyList<string> missingEndpoints)
    {
        string expectedHash = declaration.ManifestHash ?? "<none>";
        string observedHash = resolution.ManifestHash ?? "<unknown>";
        string observedSchema = resolution.SchemaVersion?.ToString() ?? "<unknown>";

        return $"External '{declaration.Name}' resolved without referenced endpoint(s) " +
            $"'{string.Join("', '", missingEndpoints)}'. Expected manifest {expectedHash} " +
            $"(export schema {ApplicationExportDocument.CurrentSchemaVersion}); observed manifest " +
            $"{observedHash} (export schema {observedSchema}). Update the provider export or bind " +
            "every endpoint named by the declaration.";
    }
}

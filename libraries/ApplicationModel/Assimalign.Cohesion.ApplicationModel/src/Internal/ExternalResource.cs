using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

internal sealed class ExternalResource : PlannedResource, IExternalResource
{
    internal const string PlanHint = "cohesion.external";
    internal const string ApplicationHint = "cohesion.external.application";
    internal const string OptionalHint = "cohesion.external.optional";
    internal const string EndpointsHint = "cohesion.external.endpoints";
    internal const string ManifestHashHint = "cohesion.external.manifest-hash";
    internal const string ClosureHashHint = "cohesion.external.closure-hash";

    private IExternalResourceResolver _resolver;

    public ExternalResource(
        ExternalResourceDeclaration declaration,
        IExternalResourceResolver? resolver = null)
        : base(GetManifest(declaration))
    {
        Declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
        _resolver = resolver ?? UnboundExternalResourceResolver.Instance;
    }

    public override string PlannerName => IsRealized
        ? nameof(GenericPlanner)
        : "External resolver";

    public ExternalResourceDeclaration Declaration { get; private set; }

    public IExternalResourceResolver Resolver => _resolver;

    public ResourceId Id => Guid.AsDeterministicGuid(
        $"{Declaration.Application}/{Declaration.Name}");

    internal bool IsRealized { get; private set; }

    internal void Bind(IExternalResourceResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    internal void Update(
        ExternalResourceDeclaration declaration,
        IExternalResourceResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        Declaration = declaration;
        UpdateManifest(GetManifest(declaration));
        Bind(resolver);
    }

    internal void Realize() => IsRealized = true;

    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ResourcePlan plan = base.CreatePlan(context);
        if (IsRealized)
        {
            return plan;
        }

        var hints = new Dictionary<string, string>(plan.Hints, StringComparer.Ordinal)
        {
            [PlanHint] = "true",
            [ApplicationHint] = Declaration.Application.ToString(),
            [OptionalHint] = Declaration.Optional.ToString(),
            [EndpointsHint] = string.Join("\u001f", Declaration.ReferencedEndpoints),
            [ClosureHashHint] = Declaration.ClosureHash,
        };

        if (Declaration.ManifestHash is string manifestHash)
        {
            hints[ManifestHashHint] = manifestHash;
        }

        return new ResourcePlan(
            plan.Schema,
            plan.Resource,
            plan.Kind,
            plan.Workload,
            plan.Container,
            plan.Volumes,
            plan.Services,
            plan.Exposures,
            hints,
            plan.ControlPlane);
    }

    private static ResourceManifest CreateManifest(ExternalResourceDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        IReadOnlyList<string> requested = declaration.ReferencedEndpoints;
        int count = requested.Count == 0 ? 1 : requested.Count;
        var endpoints = new ResourceManifestEndpoint[count];

        for (int index = 0; index < endpoints.Length; index++)
        {
            endpoints[index] = new ResourceManifestEndpoint
            {
                Name = requested.Count == 0 ? "control" : requested[index],
                Scheme = requested.Count == 0 ? "http" : "tcp",
                Protocol = "tcp",
                ContainerPort = 1,
            };
        }

        return new ResourceManifest
        {
            Name = declaration.Name,
            Kind = declaration.Kind,
            Application = declaration.Application,
            ApplicationModel = "Assimalign.Cohesion.ApplicationModel.External",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "external",
            },
            Endpoints = endpoints,
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = endpoints[0].Name,
                Path = "/cohesion/v1",
            },
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.Deployment,
            },
        };
    }

    private static ResourceManifest GetManifest(ExternalResourceDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return declaration.Manifest ?? CreateManifest(declaration);
    }
}

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A concrete <see cref="ApplicationGateway"/> for exercising the generic realization algorithm
/// with fake controllers and a real <see cref="InMemoryResourceStateManager"/>.
/// </summary>
internal sealed class TestGateway : ApplicationGateway
{
    private readonly IApplicationResourceStateManager _state;
    private readonly IReadOnlyList<IApplicationResourceController> _controllers;
    private readonly Func<IApplicationResourceDescriptor, IResourceControlContext, CancellationToken, ValueTask<ResourceInputs>>? _inputResolver;
    private readonly Func<IApplicationModel, ResourceManifest, Uri?>? _ownSecretStoreEndpointResolver;
    private readonly ResourceName _name;

    public List<string> Gathered { get; } = new();

    public ApplicationExportDocument? LastExport { get; private set; }

    public Func<IApplicationModel, IApplicationResource, IResourceControlPlane?>? DirectControlPlane { get; set; }

    public Func<ResourcePlan, TimeSpan>? ResourceReadinessBudget { get; set; }

    protected override TimeSpan GetReadinessBudget(ResourcePlan plan) =>
        ResourceReadinessBudget?.Invoke(plan) ?? base.GetReadinessBudget(plan);

    protected override bool TryGetResourceControlPlane(
        IApplicationModel model, IApplicationResource resource, out IResourceControlPlane? controlPlane)
    {
        controlPlane = DirectControlPlane?.Invoke(model, resource);
        return controlPlane is not null;
    }

    public TestGateway(
        IApplicationResourceStateManager state,
        IReadOnlyList<IApplicationResourceController> controllers,
        TimeSpan? readinessBudget = null,
        ApplicationGatewayOptions? options = null,
        Func<IApplicationResourceDescriptor, IResourceControlContext, CancellationToken, ValueTask<ResourceInputs>>? inputResolver = null,
        ResourceName? name = null,
        Func<IApplicationModel, ResourceManifest, Uri?>? ownSecretStoreEndpointResolver = null)
        : base(Configure(options, readinessBudget))
    {
        _state = state;
        _controllers = controllers;
        _inputResolver = inputResolver;
        _name = name ?? (ResourceName)"test";
        _ownSecretStoreEndpointResolver = ownSecretStoreEndpointResolver;
    }

    public override ResourceName Name => _name;

    protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

    protected override IApplicationResourceStateManager State => _state;

    protected override Task<IResourceArtifact> GatherAsync(IApplicationResource resource, CancellationToken cancellationToken)
    {
        Gathered.Add(resource.Name.ToString());
        return Task.FromResult<IResourceArtifact>(new TestArtifact(resource.Id));
    }

    protected override ValueTask<ResourceInputs> ResolveInputsAsync(
        IApplicationResourceDescriptor descriptor,
        IResourceControlContext context,
        CancellationToken cancellationToken) =>
        _inputResolver is null
            ? base.ResolveInputsAsync(descriptor, context, cancellationToken)
            : _inputResolver(descriptor, context, cancellationToken);

    protected override bool TryResolveOwnSecretStoreEndpoint(
        IApplicationModel model,
        ResourceManifest store,
        out Uri? endpoint)
    {
        endpoint = _ownSecretStoreEndpointResolver?.Invoke(model, store);
        return endpoint is not null;
    }

    protected override Task PublishApplicationExportAsync(
        ApplicationExportDocument document,
        CancellationToken cancellationToken)
    {
        LastExport = document;
        return Task.CompletedTask;
    }

    protected override Task RemoveApplicationExportAsync(
        ApplicationName application,
        CancellationToken cancellationToken) => Task.CompletedTask;

    private static ApplicationGatewayOptions Configure(
        ApplicationGatewayOptions? options,
        TimeSpan? readinessBudget)
    {
        options ??= new ApplicationGatewayOptions();
        options.ReadinessBudget = readinessBudget ?? TimeSpan.FromSeconds(30);
        options.TrustKeyRepository ??= new EphemeralTrustKeyRepository();
        return options;
    }

    private sealed class EphemeralTrustKeyRepository : IGatewayTrustKeyRepository
    {
        public Task<ECDsa> LoadOrCreateAsync(
            ApplicationName application,
            ResourceName gateway,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        }

        public Task<ECDsa> RotateAsync(
            ApplicationName application,
            ResourceName gateway,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        }
    }

    private sealed class TestArtifact : IResourceArtifact
    {
        public TestArtifact(ResourceId resource)
        {
            Resource = resource;
        }

        public ResourceId Resource { get; }
    }
}

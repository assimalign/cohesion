using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// A real <see cref="ApplicationGateway"/> over a recording controller: store resources report
/// Running on the loopback store's port, and every other resource records the inputs the gateway
/// resolved for it. Mount sources therefore flow through the gateway's real resolution path into the
/// providers a member registered.
/// </summary>
internal sealed class StoreGateway : ApplicationGateway
{
    private readonly IApplicationResourceStateManager _state = new InMemoryResourceStateManager();
    private readonly IReadOnlyList<IApplicationResourceController> _controllers;

    public StoreGateway(StoreController controller)
        : base(new ApplicationGatewayOptions
        {
            ExportDirectory = Path.Combine(
                Path.GetTempPath(),
                "cohesion-configurationstore-orchestration-set-tests",
                Guid.NewGuid().ToString("N")),
            TrustKeyRepository = new EphemeralTrustKeyRepository(),
        })
    {
        _controllers = [controller];
    }

    public override ResourceName Name => "test";

    protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

    protected override IApplicationResourceStateManager State => _state;

    protected override Task<IResourceArtifact> GatherAsync(IApplicationResource resource, CancellationToken cancellationToken) =>
        Task.FromResult<IResourceArtifact>(new StoreArtifact(resource.Id));

    protected override Task PublishApplicationExportAsync(ApplicationExportDocument document, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    protected override Task RemoveApplicationExportAsync(ApplicationName application, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private sealed class EphemeralTrustKeyRepository : IGatewayTrustKeyRepository
    {
        public Task<ECDsa> LoadOrCreateAsync(ApplicationName application, ResourceName gateway, CancellationToken cancellationToken = default) =>
            Task.FromResult(ECDsa.Create(ECCurve.NamedCurves.nistP256));

        public Task<ECDsa> RotateAsync(ApplicationName application, ResourceName gateway, CancellationToken cancellationToken = default) =>
            Task.FromResult(ECDsa.Create(ECCurve.NamedCurves.nistP256));
    }

    private sealed class StoreArtifact : IResourceArtifact
    {
        public StoreArtifact(ResourceId resource)
        {
            Resource = resource;
        }

        public ResourceId Resource { get; }
    }
}

/// <summary>
/// Reports each application's store resource Running on its loopback store's port and records the
/// inputs the gateway resolved for every other resource, keyed by <c>&lt;application&gt;/&lt;resource&gt;</c>.
/// </summary>
internal sealed class StoreController : IApplicationResourceController
{
    private readonly IReadOnlyDictionary<string, int> _storePorts;
    private readonly string _storeName;

    public StoreController(string storeName, IReadOnlyDictionary<string, int> storePorts)
    {
        _storeName = storeName;
        _storePorts = storePorts;
    }

    public Dictionary<string, ResourceInputs> Inputs { get; } = new(StringComparer.Ordinal);

    public List<string> Reconciled { get; } = new();

    public Dictionary<string, IApplicationModel> Models { get; } = new(StringComparer.Ordinal);

    public bool CanRealize(ResourcePlan plan, out string? reason)
    {
        reason = null;
        return true;
    }

    public Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string application = context.Model.Name.ToString();
        Reconciled.Add($"{application}/{context.Resource.Name}");
        Models[application] = context.Model;
        if (context.Resource.Name == (ResourceName)_storeName)
        {
            context.State.SetState(
                context.Resource.Id,
                ResourceLifecycle.Running,
                observedEndpoints: [new ResourceEndpoint("api", "http", _storePorts[application], Host: "127.0.0.1")]);
        }
        else
        {
            Inputs[$"{application}/{context.Resource.Name}"] = context.Inputs;
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Running);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(IResourceControlContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

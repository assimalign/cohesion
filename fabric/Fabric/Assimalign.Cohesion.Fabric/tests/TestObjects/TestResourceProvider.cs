using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Fabric.Tests.TestObjects;

internal sealed class TestResourceProvider : IFabricResourceProvider
{
    public TestResourceProvider(string type)
    {
        Manifest = new ResourceProviderManifest(
            [new ResourceTypeManifest(new ResourceTypeName(type), [new ApiVersion("2026-12-01")], [])],
            []);
    }

    public ResourceProviderManifest Manifest { get; }

    public FabricDiagnostics Validate(ResourceView resource, FabricValidationContext context) => FabricDiagnostics.None;

    public FabricPlan Plan(ResourceView resource, FabricEnvironment environment) => FabricPlan.Empty;

    public ValueTask<ReconcileResult> ReconcileAsync(ReconcileContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ReconcileResult(ProvisioningState.Succeeded, [], new Dictionary<string, JsonElement>(), [], [], null));

    public ValueTask<ReleaseResult> ReleaseAsync(ReconcileContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ReleaseResult(true, []));

    public ValueTask<ActionResult> InvokeActionAsync(ActionContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ActionResult(true, new Dictionary<string, JsonElement>(), []));
}

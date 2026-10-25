using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

internal sealed class VersionedStateHandler : IResourceStateHandler
{
    private ResourceStateObservation _observed = new(new ResourceStateValue(false, System.Text.Json.JsonDocument.Parse("null").RootElement.Clone()), 0, string.Empty, default);

    public VersionedStateHandler(ResourceStateDefinition definition) => Definition = definition;
    public ResourceStateDefinition Definition { get; set; }
    public System.Collections.Generic.IReadOnlyList<ResourceStateDeclaration> InitialState { get; set; } = [];
    public int Writes { get; private set; }
    public ResourceStateObservation Observation { get => _observed; set => _observed = value; }
    public ValueTask<ResourceStateValidation> ValidateAsync(ResourceStateKey key, ResourceStateValue desired, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ResourceStateValidation(true));
    public ValueTask<ResourceStateObservation> ReadAsync(ResourceStateKey key, CancellationToken cancellationToken = default) => ValueTask.FromResult(_observed);
    public ValueTask<ResourceStateApplyResult> ApplyAsync(ResourceStateApplyContext context, ResourceStateKey key, ResourceStateValue desired, CancellationToken cancellationToken = default)
    {
        if (context.Revision < _observed.Revision) { return ValueTask.FromResult(new ResourceStateApplyResult(ResourceStateApplyStatus.Stale, _observed.Revision)); }
        if (context.Revision == _observed.Revision)
        {
            return ValueTask.FromResult(new ResourceStateApplyResult(context.ValueDigest == _observed.ValueDigest ? ResourceStateApplyStatus.AlreadyCurrent : ResourceStateApplyStatus.Conflict, _observed.Revision));
        }
        Writes++;
        _observed = new ResourceStateObservation(desired, context.Revision, context.ValueDigest, context.TargetProcess);
        return ValueTask.FromResult(new ResourceStateApplyResult(ResourceStateApplyStatus.Applied, context.Revision));
    }
}

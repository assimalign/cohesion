using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// Stands in for the gateway's <see cref="IResourceSourceResolver"/>: answers configured source
/// expressions and reports every other one unresolved, recording each call.
/// </summary>
internal sealed class StubSourceResolver : IResourceSourceResolver
{
    private readonly Dictionary<string, ResourceMountInput> _inputs = new(StringComparer.Ordinal);
    private readonly List<(string Source, ResourceMountKind Kind)> _calls = new();

    internal IReadOnlyList<(string Source, ResourceMountKind Kind)> Calls => _calls.ToArray();

    internal StubSourceResolver Resolve(string source, byte[] value)
    {
        _inputs[source] = ResourceMountInput.Resolved(source, value);
        return this;
    }

    internal StubSourceResolver Unresolve(string source, string reason)
    {
        _inputs[source] = ResourceMountInput.Unresolved(source, reason);
        return this;
    }

    public ValueTask<ResourceMountInput> ResolveAsync(
        string source,
        ResourceMountKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Add((source, kind));
        return ValueTask.FromResult(_inputs.TryGetValue(source, out ResourceMountInput? input)
            ? input
            : ResourceMountInput.Unresolved(source, $"Stub source '{source}' is not configured."));
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>
/// The gateway's <see cref="IResourceSourceResolver"/>: resolves a source expression exactly as a
/// mount of the command's target would be resolved, over the same
/// <see cref="ApplicationProviders.Sources"/> registrations, so a command-input resolver never
/// needs to know how a source is served.
/// </summary>
internal sealed class GatewaySourceResolver : IResourceSourceResolver
{
    private readonly Func<string, ResourceMountKind, CancellationToken, ValueTask<ResourceMountInput>> _resolve;

    internal GatewaySourceResolver(
        Func<string, ResourceMountKind, CancellationToken, ValueTask<ResourceMountInput>> resolve)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    public ValueTask<ResourceMountInput> ResolveAsync(
        string source,
        ResourceMountKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return _resolve(source, kind, cancellationToken);
    }
}

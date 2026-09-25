using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Internal;

internal sealed class IdentityResourceCommandHandler : IResourceCommandHandler
{
    internal const string AddAudience = "identityhub.add-audience";
    internal const string AddClient = "identityhub.add-client";
    private readonly IdentityCommandRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityResourceCommandHandler"/> class.
    /// </summary>
    /// <param name="kind">The resource command kind this handler serves: <see cref="AddAudience"/> or <see cref="AddClient"/>.</param>
    /// <param name="registry">The identity command registry that applies and deletes command-delivered registrations.</param>
    public IdentityResourceCommandHandler(string kind, IdentityCommandRegistry registry)
    {
        Kind = kind;
        _registry = registry;
    }

    public string Kind { get; }

    public async ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        await _registry.MutateAsync(command, delete: false, cancellationToken).ConfigureAwait(false);
        return ReadOnlyMemory<byte>.Empty;
    }

    public async ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        await _registry.MutateAsync(command, delete: true, cancellationToken).ConfigureAwait(false);
        return ReadOnlyMemory<byte>.Empty;
    }
}

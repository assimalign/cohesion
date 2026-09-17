using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

internal sealed class IdentityResourceCommandHandler(string kind, IdentityCommandRegistry registry) : IResourceCommandHandler
{
    internal const string AddAudience = "identityhub.add-audience";
    internal const string AddClient = "identityhub.add-client";
    public string Kind { get; } = kind;

    public async ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        await registry.MutateAsync(command, delete: false, cancellationToken).ConfigureAwait(false);
        return ReadOnlyMemory<byte>.Empty;
    }

    public async ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        await registry.MutateAsync(command, delete: true, cancellationToken).ConfigureAwait(false);
        return ReadOnlyMemory<byte>.Empty;
    }
}

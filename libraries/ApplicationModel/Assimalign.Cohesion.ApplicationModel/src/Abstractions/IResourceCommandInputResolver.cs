using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Rewrites the payload of one command kind before the gateway delivers it, typically to replace
/// a declared source expression with the value it resolves to. Registered in
/// <see cref="ApplicationProviders.CommandInputs"/>.
/// </summary>
/// <remarks>
/// Declared payloads are portable desired state and never carry secret material; a resolver
/// produces the delivered payload at delivery time. A command kind with no registered resolver is
/// delivered exactly as declared, unless the target resource's manifest marks the kind
/// <see cref="ResourceManifestCommand.RequiresInputResolver"/>: then
/// <see cref="IApplicationBuilder.Build"/>, or an application set starting the member, rejects the
/// application instead of letting the resource refuse the unresolved payload at delivery.
/// </remarks>
public interface IResourceCommandInputResolver
{
    /// <summary>
    /// Gets the wire command kind this resolver handles, an area-scoped name of the form
    /// <c>&lt;area&gt;.&lt;operation&gt;</c>.
    /// </summary>
    string CommandKind { get; }

    /// <summary>
    /// Produces the payload to deliver for one declared command.
    /// </summary>
    /// <param name="declared">The command as declared in the application model.</param>
    /// <param name="sources">Resolves source expressions named by the declared payload.</param>
    /// <param name="cancellationToken">Signals that resolution should be abandoned.</param>
    /// <returns>The payload bytes to deliver in place of the declared payload.</returns>
    ValueTask<ReadOnlyMemory<byte>> ResolveAsync(
        ResourceCommandInput declared,
        IResourceSourceResolver sources,
        CancellationToken cancellationToken = default);
}

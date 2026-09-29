using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Resolves a mount-source expression the way the gateway resolves a mount. The gateway
/// implements it over the same <see cref="ApplicationProviders.Sources"/> registrations and hands
/// it to each <see cref="IResourceCommandInputResolver"/>.
/// </summary>
public interface IResourceSourceResolver
{
    /// <summary>
    /// Resolves one source expression.
    /// </summary>
    /// <param name="source">
    /// The complete expression: <c>parameter:&lt;name&gt;</c>, <c>literal:&lt;value&gt;</c>, or
    /// <c>&lt;source&gt;:&lt;key&gt;</c>.
    /// </param>
    /// <param name="kind">The mount kind the value is resolved as.</param>
    /// <param name="cancellationToken">Signals that resolution should be abandoned.</param>
    /// <returns>
    /// The resolved input, or an unresolved input carrying the actionable failure.
    /// </returns>
    ValueTask<ResourceMountInput> ResolveAsync(
        string source,
        ResourceMountKind kind,
        CancellationToken cancellationToken = default);
}

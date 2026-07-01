using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A capability interface for a resource that is realized as an executable or container:
/// it carries a conventional artifact identity and an environment-variable contract.
/// </summary>
/// <remarks>
/// Gateways pattern-match this capability (<c>resource is IExecutableResource</c>) and
/// ignore capabilities they do not understand. A local gateway resolves
/// <see cref="Artifact"/> to an executable; container gateways resolve it to the
/// pre-built image advertised by the resource's manifest package.
/// </remarks>
public interface IExecutableResource : IApplicationResource
{
    /// <summary>
    /// The conventional artifact identity, for example
    /// <c>Assimalign.Cohesion.Web.Application</c>.
    /// </summary>
    string Artifact { get; }

    /// <summary>
    /// Environment variables injected into the realized process or container. Named
    /// <c>EnvironmentVariables</c> (not <c>Environment</c>) to avoid colliding with
    /// <see cref="IApplicationModel.Environment"/>.
    /// </summary>
    IReadOnlyDictionary<string, string> EnvironmentVariables { get; }
}

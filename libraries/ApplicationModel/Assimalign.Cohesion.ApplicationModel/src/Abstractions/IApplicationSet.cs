using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Composes independently described application models into one gateway instance.
/// </summary>
public interface IApplicationSet
{
    /// <summary>Gets the environment in which member models are resolved.</summary>
    IApplicationEnvironment Environment { get; }

    /// <summary>Gets the operation selected for this application-set invocation.</summary>
    GatewayRunMode RunMode { get; }

    /// <summary>Adds an application in deterministic reconciliation order.</summary>
    /// <param name="application">The generated member application declaration.</param>
    /// <returns>This application set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="application"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// An application with the same name is already present.
    /// </exception>
    IApplicationSet AddApplication(ApplicationDeclaration application);

    /// <summary>
    /// Resolves all member models at start, composes Describe output, or dispatches the
    /// collection through one gateway for lifecycle and Render operations.
    /// </summary>
    /// <param name="cancellationToken">Signals shutdown or cancellation.</param>
    /// <returns>A task representing the application-set lifetime.</returns>
    /// <exception cref="InvalidOperationException">
    /// No applications were declared, a resolved model has the wrong identity, or the
    /// requested external realization cannot be honored.
    /// </exception>
    /// <exception cref="NotSupportedException">The selected run mode is not supported.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    Task RunAsync(CancellationToken cancellationToken = default);
}

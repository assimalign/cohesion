using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Handles one-shot command modes that are implemented by a concrete application gateway.
/// </summary>
public interface IApplicationGatewayCommandHandler
{
    /// <summary>Executes a validated gateway command and writes its protocol output.</summary>
    /// <param name="model">The built application model.</param>
    /// <param name="command">The validated command arguments.</param>
    /// <param name="cancellationToken">Cancels command execution.</param>
    /// <returns>A task that completes when the command exits.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="model"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="System.NotSupportedException">The command mode is not implemented.</exception>
    /// <exception cref="System.OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    Task ExecuteCommandAsync(
        IApplicationModel model,
        GatewayCommand command,
        CancellationToken cancellationToken = default);
}

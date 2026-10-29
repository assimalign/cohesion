using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// An infrastructure action on an application resource, such as scaling it or restarting an
/// instance. The gateway executes it against the infrastructure that runs the resource, not
/// against an instance.
/// </summary>
/// <remarks>
/// An instance's controls (<see cref="IApplicationInstanceAction"/>) change the process; an infrastructure
/// action changes the platform. It has no state for the gateway to replay: the platform keeps what
/// the action set until the resource is next deployed. The invocation and its result use the same
/// types as a control.
/// </remarks>
public interface IApplicationResourceAction
{
    /// <summary>
    /// Gets the action's name, unique among its resource's infrastructure actions.
    /// </summary>
    ActionName Name { get; }

    /// <summary>
    /// Gets the operations the action accepts: <see cref="ApplicationActionKind.Read"/>,
    /// <see cref="ApplicationActionKind.Write"/>, or both.
    /// </summary>
    ApplicationActionKind Kind { get; }

    /// <summary>
    /// Executes one invocation of the action.
    /// </summary>
    /// <param name="context">The invocation: the requested operation and its input.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The invocation's result.</returns>
    /// <exception cref="ApplicationContentException">
    /// The input is not a value of the action's input type, or the action refuses the value. A
    /// dispatcher reports it as <see cref="ApplicationActionStatus.Rejected"/>.
    /// </exception>
    ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken = default);
}

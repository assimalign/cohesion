using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// An operation a resource instance's control plane exposes, such as reading or setting a log
/// level.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Kind"/> says which operations the action accepts: <see cref="ResourceActionKind.Read"/>
/// observes the instance and <see cref="ResourceActionKind.Write"/> changes it. An action that
/// accepts both is a control, whose read reports what a write set.
/// </para>
/// <para>
/// An action with <see cref="State"/> is stateful, and only a write can change that state. After a
/// write succeeds, the control plane returns the state's snapshot, and the gateway restores it on
/// every instance of the resource that starts before the resource is next deployed. A write with no
/// state is imperative, such as flushing a cache: it changes this instance once, and nothing
/// replays it.
/// </para>
/// </remarks>
public interface IResourceAction
{
    /// <summary>
    /// Gets the action's name, unique within its resource.
    /// </summary>
    ResourceActionName Name { get; }

    /// <summary>
    /// Gets the operations the action accepts: <see cref="ResourceActionKind.Read"/>,
    /// <see cref="ResourceActionKind.Write"/>, or both.
    /// </summary>
    ResourceActionKind Kind { get; }

    /// <summary>
    /// Gets the state a write changes, or <see langword="null"/> when the action has none.
    /// </summary>
    /// <remarks>
    /// The control plane snapshots this state after each successful write, and restores a snapshot
    /// into it on request. Return the same object on every call.
    /// </remarks>
    IResourceActionState? State { get; }

    /// <summary>
    /// Executes one invocation of the action.
    /// </summary>
    /// <param name="context">The invocation: the requested operation and its input.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The invocation's result.</returns>
    /// <exception cref="ResourceContentException">
    /// The input is not a value of the action's input type, or the instance refuses the value. A
    /// control plane reports it as <see cref="ResourceActionStatus.Rejected"/>.
    /// </exception>
    ValueTask<ResourceActionResult> ExecuteAsync(ResourceActionContext context, CancellationToken cancellationToken = default);
}

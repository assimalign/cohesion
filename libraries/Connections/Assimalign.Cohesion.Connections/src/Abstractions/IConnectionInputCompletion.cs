namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Reports a clean end of input already observed by a connection's transport.
/// </summary>
/// <remarks>
/// This optional facet is found with a type test on an <see cref="IConnection"/>. It does not
/// read, wait, or consume bytes. The input pipe can still hold unread bytes after the transport
/// reports completion, so consumers must also check that its buffer is empty. A decorator that
/// buffers additional bytes must preserve that distinction when forwarding the facet.
/// </remarks>
public interface IConnectionInputCompletion
{
    /// <summary>
    /// Gets whether the peer ended input cleanly and the transport has delivered every received
    /// byte to the input pipe. A reset, abort, or failure is not a clean completion.
    /// </summary>
    /// <remarks>
    /// False is conservative: the end may not have been observed yet. This value does not
    /// indicate that the consumer has read every byte buffered by the input pipe.
    /// </remarks>
    bool IsInputCompleted { get; }
}

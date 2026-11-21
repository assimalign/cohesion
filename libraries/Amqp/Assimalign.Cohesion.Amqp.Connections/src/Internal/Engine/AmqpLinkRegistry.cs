using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The links attached on every connection one listener accepted, by the containers and name that identify each
/// (AMQP 1.0 Part 2, section 2.6.1), so a peer's attach of a link attached elsewhere can steal it.
/// </summary>
/// <remarks>
/// <para>
/// A listener creates one when <see cref="AmqpConnectionListenerOptions.AllowLinkStealing"/> is set, and every
/// connection it accepts shares it. A link is listed from the moment its <c>attach</c> is sent or received until
/// it stops, so a link that is detaching, suspended or closed is not listed and cannot be stolen.
/// </para>
/// <para>
/// Its lock is taken under a connection's lock and never the other way round: nothing here calls into a link or a
/// connection. A link listed on another connection is stolen on that connection's lock, after its own is released.
/// </para>
/// </remarks>
[DebuggerDisplay("AmqpLinkRegistry Count = {Count}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpLinkRegistry
{
    private readonly Lock _sync = new();
    private readonly Dictionary<AmqpLinkKey, AmqpLinkEndpoint> _links = new();

    /// <summary>
    /// Gets how many links are listed.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _links.Count;
            }
        }
    }

    /// <summary>
    /// Lists a link a peer attached, in place of any link listed under the same key, which it steals.
    /// </summary>
    /// <param name="key">The link's key.</param>
    /// <param name="link">The link.</param>
    /// <returns>The link listed under the key before, which the caller steals, or <see langword="null"/>.</returns>
    public AmqpLinkEndpoint? Replace(AmqpLinkKey key, AmqpLinkEndpoint link)
    {
        lock (_sync)
        {
            _links.TryGetValue(key, out AmqpLinkEndpoint? previous);
            _links[key] = link;

            return ReferenceEquals(previous, link) ? null : previous;
        }
    }

    /// <summary>
    /// Lists a link the application attaches, unless a link is already listed under its key.
    /// </summary>
    /// <param name="key">The link's key.</param>
    /// <param name="link">The link.</param>
    /// <returns><see langword="true"/> when the link was listed.</returns>
    public bool TryAdd(AmqpLinkKey key, AmqpLinkEndpoint link)
    {
        lock (_sync)
        {
            return _links.TryAdd(key, link);
        }
    }

    /// <summary>
    /// Takes a link off the list once it stops, unless another link has taken its key since.
    /// </summary>
    /// <param name="key">The link's key.</param>
    /// <param name="link">The link.</param>
    public void Remove(AmqpLinkKey key, AmqpLinkEndpoint link)
    {
        lock (_sync)
        {
            if (_links.TryGetValue(key, out AmqpLinkEndpoint? listed) && ReferenceEquals(listed, link))
            {
                _links.Remove(key);
            }
        }
    }

    private sealed class DebugView
    {
        private readonly AmqpLinkRegistry _registry;

        public DebugView(AmqpLinkRegistry registry)
        {
            _registry = registry;
        }

        public int Count => _registry.Count;
    }
}

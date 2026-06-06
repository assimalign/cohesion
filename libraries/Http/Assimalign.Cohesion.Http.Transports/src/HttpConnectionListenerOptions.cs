using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http.Transports.Internal;
using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports;

/// <summary>
/// Configures the underlying transports used by an <see cref="HttpConnectionListener"/>.
/// </summary>
public sealed class HttpConnectionListenerOptions
{
    private int _backlogCapacity = 512;

    internal List<Func<HttpConnectionTransport>> Transports { get; } = new List<Func<HttpConnectionTransport>>();

    /// <summary>
    /// Gets or sets the maximum number of accepted HTTP connections that may be buffered
    /// before producers wait for <see cref="HttpConnectionListener.AcceptOrListenAsync(System.Threading.CancellationToken)"/>
    /// to dequeue them.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the assigned value is less than <c>1</c>.
    /// </exception>
    public int BacklogCapacity
    {
        get => _backlogCapacity;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _backlogCapacity = value;
        }
    }

    /// <summary>
    /// Adds a pre-configured transport for the supplied HTTP protocol.
    /// </summary>
    /// <param name="transport">The transport to use.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transport"/> is <see langword="null"/>.</exception>
    public HttpConnectionListenerOptions UseHttp(HttpConnectionTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        return UseHttp(() => transport);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public HttpConnectionListenerOptions UseHttp(Func<HttpConnectionTransport> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Transports.Add(configure);

        return this;
    }
}
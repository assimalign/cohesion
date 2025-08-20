using System;

namespace Assimalign.Cohesion.Http.Internal;

using Assimalign.Cohesion.Transports;

internal class HttpConnectionContext
{
    /// <summary>
    /// The amount of time to wait between incoming packets being sent from client until end of request.
    /// </summary>
    internal TimeSpan ReceivingTimeout { get; init; }
    public ITransportConnection Connection { get; init; } = default!;
}

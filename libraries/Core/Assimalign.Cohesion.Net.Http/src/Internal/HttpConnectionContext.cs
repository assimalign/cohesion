using System;

namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Transports;

internal class HttpConnectionContext
{
    /// <summary>
    /// The amount of time to wait between incoming packets being sent from client until end of request.
    /// </summary>
    internal TimeSpan ReceivingTimeout { get; init; }
    public IHttpContextExecutor Executor { get; init; } = default!;
    public ITransportConnection Connection { get; init; } = default!;
}

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http1;

internal sealed class Http1ConnectionContext : HttpStreamConnectionContext
{
    public Http1ConnectionContext(ITransportConnectionContext transportContext, bool isSecure)
        : base(transportContext, isSecure)
    {
    }

    public override async IAsyncEnumerable<IHttpContext> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Http1Context? context = await Http1MessageReader.ReadRequestAsync(Stream, ConnectionInfo, GetScheme(ConnectionInfo.IsSecure), cancellationToken).ConfigureAwait(false);

            if (context is null)
            {
                yield break;
            }

            yield return context;

            if (!context.KeepAlive)
            {
                yield break;
            }
        }
    }

    public override ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        if (context is not Http1Context http1Context)
        {
            throw new System.InvalidOperationException("The supplied context does not belong to an HTTP/1.1 connection.");
        }

        // NOTE: the suppress-response-on-upgrade behaviour previously gated by
        // http1Context.ResponseFinalized is being moved to the
        // Assimalign.Cohesion.Http.ProtocolUpgrade package and needs a transport
        // <-> ProtocolUpgrade bridge to re-attach. Until that bridge lands, the
        // transport always writes the response normally. CONNECT body framing is
        // still honoured at request-parse time so request decoding stays correct.
        return Http1MessageWriter.WriteResponseAsync(Stream, http1Context, cancellationToken);
    }
}

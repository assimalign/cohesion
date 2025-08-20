using System;
using System.Web;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;


namespace Assimalign.Cohesion.Http.Internal;

using Assimalign.Cohesion.Transports;
using System.Net.WebSockets;

internal partial class Http1Connection : HttpConnection
{
    private readonly ITransportConnectionContext _context;

    public Http1Connection()
    {
       
    }

    public override ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    #region Receiving

    public override async IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (await TryReadPrefaceAsync())
        {
             
        }
    }

    [Flags]
    private enum ReadPrefaceState
    {
        None = 0,
        Preface = 1,
        Http1x = 2,
        All = Preface | Http1x
    }

    private async Task<bool> TryReadPrefaceAsync()
    {
        // HTTP/1.x and HTTP/2 support connections without TLS. That means ALPN hasn't been used to ensure both sides are
        // using the same protocol. A common problem is someone using HTTP/1.x to talk to a HTTP/2 only endpoint.
        //
        // HTTP/2 starts a connection with a preface. This method reads and validates it. If the connection doesn't start
        // with the preface, and it isn't using TLS, then we attempt to detect what the client is trying to do and send
        // back a friendly error message.
        //
        // Outcomes from this method:
        // 1. Successfully read HTTP/2 preface. Connection continues to be established.
        // 2. Detect HTTP/1.x request. Send back HTTP/1.x 400 response.
        // 3. Unknown content. Report HTTP/2 PROTOCOL_ERROR to client.
        // 4. Timeout while waiting for content.
        //
        // Future improvement: Detect TLS frame. Useful for people starting TLS connection with a non-TLS endpoint.
        var state = ReadPrefaceState.All;

        // With TLS, ALPN should have already errored if the wrong HTTP version is used.
        // Only perform additional validation if endpoint doesn't use TLS.
        //if (ConnectionFeatures.Get<ITlsHandshakeFeature>() != null)
        //{
        //    state ^= ReadPrefaceState.Http1x;
        //}

        while (_isClosed == 0)
        {
            var result = await _context.Pipe.ReadAsync();
            var readableBuffer = result.Buffer;
            var consumed = readableBuffer.Start;
            var examined = readableBuffer.End;

            try
            {
                if (!readableBuffer.IsEmpty)
                {
                    if (state.HasFlag(ReadPrefaceState.Preface))
                    {
                        if (readableBuffer.Length >= ClientPreface.Length)
                        {
                            if (IsPreface(readableBuffer, out consumed, out examined))
                            {
                                return true;
                            }
                            else
                            {
                                state ^= ReadPrefaceState.Preface;
                            }
                        }
                    }

                    if (state.HasFlag(ReadPrefaceState.Http1x))
                    {
                        if (ParseHttp1x(readableBuffer, out var detectedVersion))
                        {
                            if (detectedVersion == HttpVersion.Http10 || detectedVersion == HttpVersion.Http11)
                            {
                                Log.PossibleInvalidHttpVersionDetected(ConnectionId, HttpVersion.Http2, detectedVersion);

                                var responseBytes = InvalidHttp1xErrorResponseBytes ??= Encoding.ASCII.GetBytes(
                                    "HTTP/1.1 400 Bad Request\r\n" +
                                    "Connection: close\r\n" +
                                    "Content-Type: text/plain\r\n" +
                                    "Content-Length: 56\r\n" +
                                    "\r\n" +
                                    "An HTTP/1.x request was sent to an HTTP/2 only endpoint.");

                                await _context.Transport.Output.WriteAsync(responseBytes);

                                // Close connection here so a GOAWAY frame isn't written.
                                if (TryClose())
                                {
                                    SetConnectionErrorCode(ConnectionEndReason.InvalidHttpVersion, Http2ErrorCode.PROTOCOL_ERROR);
                                }

                                return false;
                            }
                            else
                            {
                                state ^= ReadPrefaceState.Http1x;
                            }
                        }
                    }

                    // Tested all states. Return HTTP/2 protocol error.
                    if (state == ReadPrefaceState.None)
                    {
                        throw new Http2ConnectionErrorException(CoreStrings.Http2ErrorInvalidPreface, Http2ErrorCode.PROTOCOL_ERROR, ConnectionEndReason.InvalidHandshake);
                    }
                }

                if (result.IsCompleted)
                {
                    return false;
                }
            }
            finally
            {
                Input.AdvanceTo(consumed, examined);

                UpdateConnectionState();
            }
        }

        return false;
    }



    #endregion




    public override IAsyncEnumerable<IHttpContext> SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    internal override async IAsyncEnumerable<IHttpContext> ProcessAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var context = new Http1Context();

            try
            {
              
            }
            catch (Exception exception)
            {
                connection.Abort();
                break;
            }

            yield return context;
        }
    }
}
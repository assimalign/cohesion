#if NET7_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public sealed class QuicServerTransportOptions
{
    private long _defaultStreamErrorCode;
    private long _defaultCloseErrorCode;


    /// <summary>
    /// 
    /// </summary>
    public IPEndPoint EndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8080);

    /// <summary>
	/// The maximum length of the pending connection middleware.
	/// </summary>
	/// <remarks>
	/// Defaults to 512.
	/// </remarks>
	public int Backlog { get; set; } = 512;

    /// <summary>
    /// The maximum number of concurrent bi-directional streams per connection.
    /// </summary>
    public int MaxBidirectionalStreamCount { get; set; } = 100;

    /// <summary>
    /// The maximum number of concurrent inbound uni-directional streams per connection.
    /// </summary>
    public int MaxUnidirectionalStreamCount { get; set; } = 10;

    /// <summary>
    /// The maximum read size.
    /// </summary>
    public long? MaxReadBufferSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// The maximum write size.
    /// </summary>
    public long? MaxWriteBufferSize { get; set; } = 64 * 1024;

    /// <summary>
    /// 
    /// </summary>
    public List<SslApplicationProtocol> AcceptApplicationProtocols { get; set; } = new List<SslApplicationProtocol>()
    {
        SslApplicationProtocol.Http3
    };

    /// <summary>
    /// 
    /// </summary>
    public SslServerAuthenticationOptions? ServerAuthenticationOptions { get; set; }


    /// <summary>
    /// Error code used when the stream needs to abort the read or write side of the stream internally.
    /// </summary>
    public long DefaultStreamErrorCode
    {
        get => _defaultStreamErrorCode;
        set
        {
            ValidateErrorCode(value);
            _defaultStreamErrorCode = value;
        }
    }

    /// <summary>
    /// Error code used when an open connection is disposed.
    /// </summary>
    public long DefaultCloseErrorCode
    {
        get => _defaultCloseErrorCode;
        set
        {
            ValidateErrorCode(value);
            _defaultCloseErrorCode = value;
        }
    }

    internal static void ValidateErrorCode(long errorCode)
    {
        const long MinErrorCode = 0;
        const long MaxErrorCode = (1L << 62) - 1;

        if (errorCode < MinErrorCode || errorCode > MaxErrorCode)
        {
            // Print the values in hex since the max is unintelligible in decimal
            throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode, $"A value between 0x{MinErrorCode:x} and 0x{MaxErrorCode:x} is required.");
        }
    }

    internal TimeProvider TimeProvider = TimeProvider.System;
}
#endif
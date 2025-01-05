using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal readonly struct SocketPipeResult
{
    public readonly SocketException Error = null!;


    [MemberNotNullWhen(true, nameof(Error))]
    public readonly bool HasError => Error != null;

    public SocketPipeResult(int bytesTransferred)
    {
        BytesTransferred = bytesTransferred;
    }

    public SocketPipeResult(SocketException exception)
    {
        Error = exception;
        BytesTransferred = 0;
    }

    public readonly bool IsSuccess => Error == null;
    public readonly int BytesTransferred;


    public bool IsNormalCompletion(out Exception? exception)
    {
        exception = null;

        if (IsSuccess)
        {
            return true;
        }
        if (IsConnectionResetError(Error.SocketErrorCode))
        {
            // This could be ignored if _shutdownReason is already set.
            var ex = Error;

            return false;
        }
        if (IsConnectionAbortError(Error.SocketErrorCode))
        {
            // This exception should always be ignored because _shutdownReason should be set.
            exception = Error;

            return false;
        }

        // This is unexpected.
        exception = Error;

        return false;
    }

    private static bool IsConnectionResetError(SocketError errorCode)
    {
        return errorCode == SocketError.ConnectionReset ||
               errorCode == SocketError.Shutdown ||
               (errorCode == SocketError.ConnectionAborted && OperatingSystem.IsWindows());
    }
    private static bool IsConnectionAbortError(SocketError errorCode)
    {
        // Calling Dispose after ReadAsync can cause an "InvalidArgument" error on *nix.
        return errorCode == SocketError.OperationAborted ||
               errorCode == SocketError.Interrupted ||
               (errorCode == SocketError.InvalidArgument && !OperatingSystem.IsWindows());
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal static class SocketHelper
{
    internal static bool IsConnectionResetError(SocketError errorCode)
    {
        return errorCode == SocketError.ConnectionReset ||
               errorCode == SocketError.Shutdown ||
               (errorCode == SocketError.ConnectionAborted && OperatingSystem.IsWindows());
    }
    internal static bool IsConnectionAbortError(SocketError errorCode)
    {
        // Calling Dispose after ReadAsync can cause an "InvalidArgument" error on *nix.
        return errorCode == SocketError.OperationAborted ||
               errorCode == SocketError.Interrupted ||
               (errorCode == SocketError.InvalidArgument && !OperatingSystem.IsWindows());
    }
}

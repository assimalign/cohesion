using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal class SocketConnectionResetException : TransportException
{
    public SocketConnectionResetException(string message) 
        : base(message) { }

    public SocketConnectionResetException(string message, Exception inner) 
        : base(message, inner) { }
}

#if NET7_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Transports.Internal;

public sealed class QuicClientTransportContext : ITransportContext
{
    internal QuicClientTransportContext(QuicTransportConnection connection)
    {
        Connection = connection;
    }

    public ITransportConnection Connection { get; }
}
#endif
using Assimalign.Cohesion.Transports;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Transports.Internal
{
    internal class HttpStreamConnectionInfo : HttpConnectionInfo
    {

        public HttpStreamConnectionInfo(ITransportConnectionContext context)
        {
            
        }

        public override CancellationToken ConnectionAborted => base.ConnectionAborted;

        public override ValueTask AbortAsync()
        {
            return base.AbortAsync();
        }
    }
}

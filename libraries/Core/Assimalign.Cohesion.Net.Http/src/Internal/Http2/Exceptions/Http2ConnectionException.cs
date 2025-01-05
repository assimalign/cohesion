using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class Http2ConnectionException : HttpException
{
    public Http2ConnectionException(string message) : base(message)
    {
    }
}

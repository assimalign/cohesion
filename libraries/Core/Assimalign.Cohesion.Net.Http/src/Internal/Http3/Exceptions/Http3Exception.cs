using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class Http3Exception : HttpException
{
    public Http3Exception(string message) : base(message)
    {
    }

    public Http3Exception(string message, Exception inner) : base(message, inner)
    {
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class Http1InvalidRequestMessageException : HttpException
{
    public Http1InvalidRequestMessageException(string message) : base(message)
    {
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

internal class HttpInvalidMethodException : HttpException
{
    public HttpInvalidMethodException(string message) : base(message)
    {
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class Http1Exception : HttpException
{
    public Http1Exception(string message) : base(message)
    {
    }

    public Http1Exception(string message, Exception inner) : base(message, inner)
    {
    }

    public static Http1Exception InvalidRequest(Exception inner = null!)
    {
        return new Http1Exception("The HTTP request could not be parsed", inner)
        {
            Code = HttpExceptionCode.ReadingError
        };
    }
}

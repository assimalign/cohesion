using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Http;

[Flags]
public enum HttpProtocol
{
    Http1,
    Http2,
    Http3
}

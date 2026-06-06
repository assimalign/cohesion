using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Text;

namespace Assimalign.Cohesion.Http.Transports.Internal;

[EventSource(Name = name)]
internal class HttpTransportEventSource : EventSource
{
    public const string name = nameof(Assimalign) + "." + nameof(Cohesion) + "." + nameof(Http) + "." + nameof(Transports);
}

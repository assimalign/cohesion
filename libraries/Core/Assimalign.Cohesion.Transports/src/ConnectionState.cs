using System;

namespace Assimalign.Cohesion.Transports;

public enum ConnectionState
{
    Idle = 0,
    Opening = 1,
    Open = 2,
    Aborted = 3,
    Closing = 4,
    Closed = 5
}
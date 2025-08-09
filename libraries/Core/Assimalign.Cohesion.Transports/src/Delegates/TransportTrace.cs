using System.Collections.Generic;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// A delegate for collecting trace information from the transport layer.
/// </summary>
/// <remarks>
/// It might be useful to use a lock method when utilizing this delegate 
/// as it is possible for multiple threads to access this delegate at the same time.
/// </remarks>
/// <param name="items"></param>
/// <param name="traceCode"></param>
/// <param name="message"></param>
public delegate void TransportTrace(object? traceCode, IDictionary<string, object?> items, string? message = null);
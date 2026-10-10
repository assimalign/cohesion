using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>The status code and header fields of a handshake response.</summary>
internal sealed record RawHandshakeResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;
}

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Injects the server's precomputed RFC 7838 <c>Alt-Svc</c> advertisement into a response's headers
/// at head-commit time, shared by the HTTP/1.1 and HTTP/2 response-head write paths so both advertise
/// identically.
/// </summary>
internal static class HttpAltServiceInjector
{
    /// <summary>
    /// Adds <paramref name="altSvcHeaderValue"/> as the <c>Alt-Svc</c> header when the server has an
    /// advertisement to make and the application did not already set the header. Applied just before
    /// the head is serialized, so an application-set value always wins (RFC 7838 — the server never
    /// overwrites an application's <c>Alt-Svc</c>).
    /// </summary>
    /// <param name="headers">The response headers about to be committed.</param>
    /// <param name="altSvcHeaderValue">
    /// The precomputed <c>Alt-Svc</c> field value, or <see langword="null"/> when advertisement is
    /// off (no injection occurs).
    /// </param>
    public static void Inject(HttpHeaderCollection headers, string? altSvcHeaderValue)
    {
        if (altSvcHeaderValue is not null && !headers.ContainsKey(HttpHeaderKey.AltSvc))
        {
            headers[HttpHeaderKey.AltSvc] = altSvcHeaderValue;
        }
    }
}

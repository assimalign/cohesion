using System;

namespace Assimalign.Cohesion.Cli;

internal static class HttpEndpoint
{
    internal static Uri Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new CliException("Endpoint must be an absolute HTTP(S) URL without user information, query or fragment.");
        }
        return endpoint;
    }
}

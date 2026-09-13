using System;
using System.Net.Http;

namespace Assimalign.Cohesion.Rezolvr.Client;

/// <summary>Creates clients for the Rezolvr resource's HTTP admin command endpoint.</summary>
public static class RezolvrCommandClient
{
    /// <summary>Creates a command client without performing network I/O.</summary>
    /// <param name="controlPlaneAddress">The full HTTP control-plane base address, including its manifest path.</param>
    /// <param name="bearerToken">The opaque bootstrap credential.</param>
    /// <returns>A caller-owned command client.</returns>
    /// <exception cref="ArgumentException">The address is not HTTP(S) or the credential is blank.</exception>
    public static IRezolvrCommandClient Create(Uri controlPlaneAddress, string bearerToken)
    {
        Uri.ThrowIfNotEndpoint(controlPlaneAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        if (controlPlaneAddress.Scheme != Uri.UriSchemeHttp && controlPlaneAddress.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The rezolvr control-plane address must use HTTP or HTTPS.", nameof(controlPlaneAddress));
        }
        return new HttpRezolvrCommandClient(controlPlaneAddress, bearerToken,
            new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }));
    }
}

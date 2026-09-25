using System;
using System.Net.Http;

using Assimalign.Cohesion.Rezolvr.Client.Internal;

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
        ValidateArguments(controlPlaneAddress, bearerToken);
        return Create(controlPlaneAddress, bearerToken,
            new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }),
            ownsTransport: true);
    }

    /// <summary>Creates a command client with a caller-owned transport, including its TLS trust policy.</summary>
    /// <param name="controlPlaneAddress">The full HTTP(S) control-plane address, including its manifest path.</param>
    /// <param name="bearerToken">The opaque bootstrap credential.</param>
    /// <param name="transport">The transport used to deliver commands.</param>
    /// <returns>A command client using the supplied transport.</returns>
    /// <remarks>The transport is owned by the caller: disposing the returned client does not dispose it.</remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The address is not HTTP(S) or the credential is blank.</exception>
    public static IRezolvrCommandClient Create(
        Uri controlPlaneAddress, string bearerToken, HttpMessageInvoker transport)
    {
        ValidateArguments(controlPlaneAddress, bearerToken);
        ArgumentNullException.ThrowIfNull(transport);
        return Create(controlPlaneAddress, bearerToken, transport, ownsTransport: false);
    }

    private static IRezolvrCommandClient Create(
        Uri controlPlaneAddress, string bearerToken, HttpMessageInvoker transport, bool ownsTransport) =>
        new HttpRezolvrCommandClient(controlPlaneAddress, bearerToken, transport, ownsTransport);

    private static void ValidateArguments(Uri controlPlaneAddress, string bearerToken)
    {
        Uri.ThrowIfNotEndpoint(controlPlaneAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        if (controlPlaneAddress.Scheme != Uri.UriSchemeHttp && controlPlaneAddress.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The rezolvr control-plane address must use HTTP or HTTPS.", nameof(controlPlaneAddress));
        }
    }
}

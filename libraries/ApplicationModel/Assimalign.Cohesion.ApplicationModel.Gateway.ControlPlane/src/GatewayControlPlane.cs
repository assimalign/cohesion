using System;
using System.IO;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane;

/// <summary>
/// Creates the hosting-free gateway control-plane server and authenticated resolver client.
/// </summary>
public static class GatewayControlPlane
{
    /// <summary>Creates a gateway control-plane factory with default options.</summary>
    /// <returns>A factory suitable for <see cref="ApplicationGatewayOptions.ControlPlane"/>.</returns>
    public static IApplicationGatewayControlPlaneFactory CreateFactory() =>
        new GatewayControlPlaneFactory(new GatewayControlPlaneOptions());

    /// <summary>Creates a gateway control-plane factory with configured options.</summary>
    /// <param name="configure">Configures metadata publication, validation time, and dispatchers.</param>
    /// <returns>A factory suitable for <see cref="ApplicationGatewayOptions.ControlPlane"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    public static IApplicationGatewayControlPlaneFactory CreateFactory(
        Action<GatewayControlPlaneOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new GatewayControlPlaneOptions();
        configure(options);
        return new GatewayControlPlaneFactory(options);
    }

    /// <summary>
    /// Creates a client whose credential and trusted peers are supplied by an active application gateway.
    /// </summary>
    /// <returns>An authenticated control-plane client.</returns>
    public static IAuthenticatedControlPlaneClient CreateClient() =>
        new GatewayControlPlaneClient();

    /// <summary>Creates a fixed-credential client for direct resolver and application-set use.</summary>
    /// <param name="bearerToken">The bearer token sent to the peer gateway.</param>
    /// <param name="trustedIssuer">The expected peer application and public trust key.</param>
    /// <returns>An authenticated control-plane client.</returns>
    /// <exception cref="ArgumentException"><paramref name="bearerToken"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="trustedIssuer"/> is <see langword="null"/>.</exception>
    public static IAuthenticatedControlPlaneClient CreateClient(
        string bearerToken,
        TrustedIssuer trustedIssuer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        ArgumentNullException.ThrowIfNull(trustedIssuer);
        return new GatewayControlPlaneClient(bearerToken, trustedIssuer);
    }

    /// <summary>
    /// Installs SDK defaults without replacing explicitly configured server or client seams.
    /// </summary>
    /// <param name="options">The gateway options to complete.</param>
    /// <param name="runMode">The selected gateway command mode.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static void Configure(ApplicationGatewayOptions options, GatewayRunMode runMode)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ControlPlaneClient ??= CreateClient();
        if (runMode is not (GatewayRunMode.Run or GatewayRunMode.Apply) || options.ControlPlane is not null)
        {
            return;
        }

        string metadataDirectory = options switch
        {
            LocalGatewayOptions { StateDirectory: { Length: > 0 } stateDirectory } => stateDirectory,
            { ExportDirectory: { Length: > 0 } exportDirectory } => exportDirectory,
            _ => Path.Combine(Environment.CurrentDirectory, ".cohesion"),
        };

        options.ControlPlane = CreateFactory(controlPlane =>
        {
            controlPlane.MetadataDirectory = metadataDirectory;
            controlPlane.TimeProvider = options.TimeProvider;
        });
    }
}

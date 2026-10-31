using System;
using System.Collections.Generic;
using System.Globalization;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// The base of every area's resource options: the platforms the resource runs on, each through its
/// gateway client, and the deployment a platform started the instance with.
/// </summary>
/// <remarks>
/// <para>
/// An area's options derive from it, so a platform's hosting package adds its verb, such as
/// <c>UseInProcess()</c>, to the resources of every area at once. The verb extends the options
/// through a generic receiver, so it returns the area's own options for chaining.
/// </para>
/// <para>
/// When the area builds the resource it reads <see cref="GetGatewayClient"/>. The instance runs
/// unmanaged when its environment names no platform, and fails to build when the resource uses no
/// client for the platform it names, rather than run unmanaged where a gateway expects it to publish.
/// </para>
/// <para>
/// The defaults come from the instance's environment, read through <see cref="AppEnvironment"/> when
/// the options are created, so a gateway that runs the instance in its own process sets them for this
/// instance alone.
/// </para>
/// </remarks>
public abstract class ApplicationInstanceOptions
{
    private readonly List<IApplicationResourceInstanceGatewayClient> _clients = [];

    /// <summary>
    /// Initializes the options from the instance's environment.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The environment names a platform that is not a platform name, or a deployment that is not a
    /// deployment id.
    /// </exception>
    protected ApplicationInstanceOptions()
    {
        Platform = ReadPlatform();
        Deployment = ReadDeployment();
    }

    /// <summary>
    /// Gets or sets the platform whose gateway runs the instance; <see langword="null"/> runs the
    /// instance unmanaged.
    /// </summary>
    /// <remarks>
    /// Defaults to the <see cref="AppEnvironment.Variables.Gateway"/> variable, which a gateway sets to
    /// its platform's name for every instance it starts.
    /// </remarks>
    public PlatformName? Platform { get; set; }

    /// <summary>
    /// Gets or sets the deployment that started the instance, which it repeats in its manifest;
    /// <see langword="null"/> for an instance no gateway deployed.
    /// </summary>
    /// <remarks>
    /// Defaults to the <see cref="AppEnvironment.Variables.Deployment"/> variable the gateway's
    /// platform sets. The gateway counts its deployment ready only once an instance that repeats the
    /// deployment's id publishes.
    /// </remarks>
    public DeploymentId? Deployment { get; set; }

    /// <summary>
    /// Gets the gateway clients the resource uses, at most one per platform, in the order they were
    /// added.
    /// </summary>
    public IReadOnlyList<IApplicationResourceInstanceGatewayClient> GatewayClients => _clients;

    /// <summary>
    /// Opts the resource into a platform: adds the client its instances reach that platform's gateway
    /// through.
    /// </summary>
    /// <remarks>
    /// A platform's hosting package calls it from its own verb; an application calls it directly only
    /// for a client no package ships.
    /// </remarks>
    /// <param name="client">The platform's client.</param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The client names no platform, or the resource already uses a client for its platform.
    /// </exception>
    public ApplicationInstanceOptions UseGatewayClient(IApplicationResourceInstanceGatewayClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        PlatformName platform = client.Platform;
        if (platform.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayClientPlatformMissing, nameof(client));
        }

        foreach (IApplicationResourceInstanceGatewayClient used in _clients)
        {
            if (used.Platform == platform)
            {
                throw new ArgumentException(Format(ErrorMessages.GatewayClientDuplicate, platform), nameof(client));
            }
        }

        _clients.Add(client);
        return this;
    }

    /// <summary>
    /// Gets the client of the platform <see cref="Platform"/> names: the one the instance publishes
    /// through and its gateway calls it through.
    /// </summary>
    /// <returns>The client, or <see langword="null"/> when no platform is named and the instance runs unmanaged.</returns>
    /// <exception cref="InvalidOperationException">The resource uses no client for the named platform.</exception>
    public IApplicationResourceInstanceGatewayClient? GetGatewayClient()
    {
        if (Platform is not { } platform)
        {
            return null;
        }

        foreach (IApplicationResourceInstanceGatewayClient client in _clients)
        {
            if (client.Platform == platform)
            {
                return client;
            }
        }

        throw new InvalidOperationException(Format(ErrorMessages.GatewayClientMissing, platform));
    }

    private static PlatformName? ReadPlatform()
    {
        string? value = AppEnvironment.GetValue(AppEnvironment.Variables.Gateway);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return PlatformName.TryCreate(value, out PlatformName platform)
            ? platform
            : throw new InvalidOperationException(Format(ErrorMessages.PlatformVariableInvalid, AppEnvironment.Variables.Gateway, value));
    }

    private static DeploymentId? ReadDeployment()
    {
        string? value = AppEnvironment.GetValue(AppEnvironment.Variables.Deployment);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DeploymentId.TryParse(value, out DeploymentId deployment)
            ? deployment
            : throw new InvalidOperationException(Format(ErrorMessages.DeploymentVariableInvalid, AppEnvironment.Variables.Deployment, value));
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// The base of every area's resource options: the platforms the resource runs on, each through its
/// gateway client, the deployment a platform started the instance with, and the actions the
/// application adds to the resource's own.
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
/// <para>
/// An application adds actions of its own with <see cref="AddAction(IApplicationInstanceAction, JsonSerializerContext)"/>
/// on any area's resource. The area creates them with <see cref="CreateActions"/> once the
/// application's services exist, lists them after the resource's own actions, and writes their
/// content through <see cref="JsonContexts"/>.
/// </para>
/// </remarks>
public abstract class ApplicationInstanceOptions
{
    private readonly List<IApplicationInstanceGatewayClient> _clients = [];
    private readonly List<(Func<IServiceProvider, IApplicationInstanceAction> Factory, bool Owned)> _actions = [];
    private readonly List<JsonSerializerContext> _jsonContexts = [];

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
    public IReadOnlyList<IApplicationInstanceGatewayClient> GatewayClients => _clients;

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
    public ApplicationInstanceOptions UseGatewayClient(IApplicationInstanceGatewayClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        PlatformName platform = client.Platform;
        if (platform.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayClientPlatformMissing, nameof(client));
        }

        foreach (IApplicationInstanceGatewayClient used in _clients)
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
    public IApplicationInstanceGatewayClient? GetGatewayClient()
    {
        if (Platform is not { } platform)
        {
            return null;
        }

        foreach (IApplicationInstanceGatewayClient client in _clients)
        {
            if (client.Platform == platform)
            {
                return client;
            }
        }

        throw new InvalidOperationException(Format(ErrorMessages.GatewayClientMissing, platform));
    }

    /// <summary>
    /// Gets the source-generated contexts the added actions came with, in the order they were first
    /// added, each once.
    /// </summary>
    /// <remarks>
    /// The area writes control-plane content through them, after its own context, as
    /// <c>JsonApplicationContentSerializer.Create([areaContext, .. options.JsonContexts])</c>.
    /// </remarks>
    public IReadOnlyList<JsonSerializerContext> JsonContexts => _jsonContexts;

    /// <summary>
    /// Adds an action the application defines to the resource, beside the actions the resource
    /// exposes itself.
    /// </summary>
    /// <remarks>
    /// The action is published in the resource's manifest and served by its control plane like the
    /// resource's own, which it follows. Its name must differ from every other action's: a name the
    /// resource's own actions or another added action already use fails the application's build.
    /// </remarks>
    /// <param name="action">The action.</param>
    /// <param name="jsonContext">
    /// The source-generated context that declares the action's input, output, and state types;
    /// <see langword="null"/> when the action exchanges no content of its own.
    /// </param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    public ApplicationInstanceOptions AddAction(IApplicationInstanceAction action, JsonSerializerContext? jsonContext = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        return Add(_ => action, owned: false, jsonContext);
    }

    /// <summary>
    /// Adds an action the application defines to the resource, created from the application's
    /// services once the application is built.
    /// </summary>
    /// <remarks>
    /// The factory runs once, when the area creates the resource's control plane from the
    /// application's container, so the action can resolve the services the application registered.
    /// Its name must differ from every other action's: a name the resource's own actions or another
    /// added action already use fails the application's build.
    /// <para>
    /// Nothing owns the action a factory creates: it is not a container registration, so the
    /// application's provider never disposes it. A factory therefore returns an action that is neither
    /// <see cref="IDisposable"/> nor <see cref="IAsyncDisposable"/>, and a disposable one fails the
    /// application's build. An action that needs a disposable collaborator resolves it from the
    /// services, which own it. An action added as an instance is the caller's and may be disposable.
    /// </para>
    /// </remarks>
    /// <param name="factory">Creates the action from the application's services.</param>
    /// <param name="jsonContext">
    /// The source-generated context that declares the action's input, output, and state types;
    /// <see langword="null"/> when the action exchanges no content of its own.
    /// </param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public ApplicationInstanceOptions AddAction(Func<IServiceProvider, IApplicationInstanceAction> factory, JsonSerializerContext? jsonContext = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return Add(factory, owned: true, jsonContext);
    }

    /// <summary>
    /// Creates the actions the application added, in the order they were added.
    /// </summary>
    /// <remarks>
    /// The area calls it once, from the factory that creates the resource's control plane, so each
    /// factory runs once against the application's services.
    /// </remarks>
    /// <param name="provider">The application's services.</param>
    /// <returns>The added actions; empty when the application added none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// An action factory returned <see langword="null"/>, or a disposable action, which nothing would
    /// dispose.
    /// </exception>
    public IReadOnlyList<IApplicationInstanceAction> CreateActions(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var actions = new IApplicationInstanceAction[_actions.Count];
        for (int i = 0; i < actions.Length; i++)
        {
            (Func<IServiceProvider, IApplicationInstanceAction> factory, bool owned) = _actions[i];
            IApplicationInstanceAction action = factory.Invoke(provider)
                ?? throw new InvalidOperationException(ErrorMessages.ActionFactoryResultNull);

            // The container never sees a factory-created action, so it would never dispose one.
            if (owned && action is IDisposable or IAsyncDisposable)
            {
                throw new InvalidOperationException(Format(ErrorMessages.ActionFactoryResultDisposable, action.Name, action.GetType()));
            }

            actions[i] = action;
        }

        return actions;
    }

    private ApplicationInstanceOptions Add(Func<IServiceProvider, IApplicationInstanceAction> factory, bool owned, JsonSerializerContext? jsonContext)
    {
        _actions.Add((factory, owned));
        if (jsonContext is not null && !_jsonContexts.Contains(jsonContext))
        {
            _jsonContexts.Add(jsonContext);
        }

        return this;
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

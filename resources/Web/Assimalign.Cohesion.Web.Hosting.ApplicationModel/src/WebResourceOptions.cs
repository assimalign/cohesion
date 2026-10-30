using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel;

/// <summary>
/// Configures the Web resource that <c>builder.AddResource(name, ...)</c> adds: where it publishes
/// its manifest, where it serves its control plane, and the actions the application adds to the
/// resource's own.
/// </summary>
/// <remarks>
/// <para>
/// The resource's own actions come from the settings the application's features registered, such as
/// the <c>cors</c> action over the setting <c>builder.Services.AddCors(...)</c> registers. They and
/// their content types belong to the resource and are not configured here.
/// </para>
/// <para>
/// Control-plane content is JSON. Each action's input, output, and state types are written through
/// the source-generated context that declares them: the resource's own for its actions, and the one
/// passed with <see cref="AddAction(IApplicationInstanceAction, JsonSerializerContext)"/> for an action the
/// application adds.
/// </para>
/// </remarks>
public sealed class WebResourceOptions
{
    private readonly List<Func<WebApplicationContext, IApplicationInstanceAction>> _actions = [];
    private readonly List<JsonSerializerContext> _jsonContexts = [];

    internal WebResourceOptions(ResourceName name)
    {
        Name = name;
        ManifestEndpoint = ReadEndpoint(AppEnvironment.Variables.ManifestEndpoint);
        ControlPlaneEndpoint = ReadEndpoint(AppEnvironment.Variables.ControlPlaneEndpoint);
        Deployment = ReadDeployment();
    }

    /// <summary>
    /// Gets the resource's name.
    /// </summary>
    public ResourceName Name { get; }

    /// <summary>
    /// Gets or sets the URL the instance publishes its manifest to; <see langword="null"/> runs the
    /// application unmanaged.
    /// </summary>
    /// <remarks>
    /// Defaults to the <see cref="AppEnvironment.Variables.ManifestEndpoint"/> variable the
    /// provisioning gateway sets.
    /// </remarks>
    public Uri? ManifestEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the URL the instance serves its control plane on, a private listener separate
    /// from the application's own. Required when <see cref="ManifestEndpoint"/> is set.
    /// </summary>
    /// <remarks>
    /// Defaults to the <see cref="AppEnvironment.Variables.ControlPlaneEndpoint"/> variable the
    /// provisioning gateway sets.
    /// </remarks>
    public Uri? ControlPlaneEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the deployment that started the instance, which it repeats in its manifest;
    /// <see langword="null"/> for an instance no gateway deployed.
    /// </summary>
    /// <remarks>
    /// Defaults to the <see cref="AppEnvironment.Variables.Deployment"/> variable the provisioning
    /// gateway's platform sets. The gateway counts its deployment ready only once an instance that
    /// repeats the deployment's id publishes.
    /// </remarks>
    public DeploymentId? Deployment { get; set; }

    /// <summary>
    /// Gets or sets the callback that creates the HTTP client the instance publishes its manifest
    /// with; <see langword="null"/> uses a client over a handler shared by the whole process.
    /// </summary>
    /// <remarks>
    /// Called once per publication; the client is disposed afterward. Return one from an HTTP client
    /// factory to share its pooled handlers: <c>() =&gt; clients.Create("gateway")</c>.
    /// </remarks>
    public Func<HttpClient>? HttpClientFactory { get; set; }

    internal IReadOnlyList<Func<WebApplicationContext, IApplicationInstanceAction>> Actions => _actions;

    internal IReadOnlyList<JsonSerializerContext> JsonContexts => _jsonContexts;

    /// <summary>
    /// Adds an action the application defines to the resource, beside the actions the resource
    /// exposes from the application's settings.
    /// </summary>
    /// <remarks>
    /// The action is published in the resource's manifest and served by its control plane like the
    /// resource's own. Its name must differ from every other action's: a name the resource's own
    /// actions or another added action already use fails the application's build.
    /// </remarks>
    /// <param name="action">The action.</param>
    /// <param name="jsonContext">
    /// The source-generated context that declares the action's input, output, and state types;
    /// <see langword="null"/> when the action exchanges no content of its own.
    /// </param>
    /// <returns>The same options for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    public WebResourceOptions AddAction(IApplicationInstanceAction action, JsonSerializerContext? jsonContext = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        return AddAction(_ => action, jsonContext);
    }

    /// <summary>
    /// Adds an action the application defines to the resource, created with the application's
    /// context once the application is built.
    /// </summary>
    /// <remarks>
    /// The factory runs once, when the resource's control plane is created, after the application's
    /// container exists, so the action can use the application's services through
    /// <see cref="WebApplicationContext.ServiceProvider"/>. Its name must differ from every other
    /// action's: a name the resource's own actions or another added action already use fails the
    /// application's build.
    /// </remarks>
    /// <param name="factory">Creates the action from the application's context.</param>
    /// <param name="jsonContext">
    /// The source-generated context that declares the action's input, output, and state types;
    /// <see langword="null"/> when the action exchanges no content of its own.
    /// </param>
    /// <returns>The same options for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public WebResourceOptions AddAction(Func<WebApplicationContext, IApplicationInstanceAction> factory, JsonSerializerContext? jsonContext = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _actions.Add(factory);
        if (jsonContext is not null && !_jsonContexts.Contains(jsonContext))
        {
            _jsonContexts.Add(jsonContext);
        }

        return this;
    }

    // Read through AppEnvironment, so an application a gateway runs in its own process reads the
    // values its scope holds, not the process's.
    private static Uri? ReadEndpoint(string variable)
    {
        string? value = AppEnvironment.GetValue(variable);

        return string.IsNullOrWhiteSpace(value) ? null : new Uri(value, UriKind.Absolute);
    }

    private static DeploymentId? ReadDeployment()
    {
        string? value = AppEnvironment.GetValue(AppEnvironment.Variables.Deployment);

        return string.IsNullOrWhiteSpace(value) ? null : DeploymentId.Parse(value);
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel;

/// <summary>
/// Configures the Web resource that <c>builder.Services.AddResource(name, ...)</c> registers: the
/// platforms it runs on, through the options every area's resource shares, and the actions the
/// application adds to the resource's own.
/// </summary>
/// <remarks>
/// <para>
/// A platform's hosting package opts the resource into its platform, such as
/// <c>resource =&gt; resource.UseInProcess()</c>; the platform owns how the instance reaches its
/// gateway, so nothing here configures a transport.
/// </para>
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
public sealed class WebResourceOptions : ApplicationInstanceOptions
{
    private readonly List<Func<IServiceProvider, IApplicationInstanceAction>> _actions = [];
    private readonly List<JsonSerializerContext> _jsonContexts = [];

    internal WebResourceOptions(ResourceName name)
    {
        Name = name;
    }

    /// <summary>
    /// Gets the resource's name.
    /// </summary>
    public ResourceName Name { get; }

    internal IReadOnlyList<Func<IServiceProvider, IApplicationInstanceAction>> Actions => _actions;

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
    /// Adds an action the application defines to the resource, created from the application's services
    /// once the application is built.
    /// </summary>
    /// <remarks>
    /// The factory runs once, when the container creates the resource's control plane, so the action
    /// can resolve the services the application registered. Its name must differ from every other
    /// action's: a name the resource's own actions or another added action already use fails the
    /// application's build.
    /// </remarks>
    /// <param name="factory">Creates the action from the application's services.</param>
    /// <param name="jsonContext">
    /// The source-generated context that declares the action's input, output, and state types;
    /// <see langword="null"/> when the action exchanges no content of its own.
    /// </param>
    /// <returns>The same options for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public WebResourceOptions AddAction(Func<IServiceProvider, IApplicationInstanceAction> factory, JsonSerializerContext? jsonContext = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _actions.Add(factory);
        if (jsonContext is not null && !_jsonContexts.Contains(jsonContext))
        {
            _jsonContexts.Add(jsonContext);
        }

        return this;
    }
}

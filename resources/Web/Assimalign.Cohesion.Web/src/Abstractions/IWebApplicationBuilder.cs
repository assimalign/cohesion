using System;

namespace Assimalign.Cohesion.Web;

using Http;


/// <summary>
/// Composes a web application: the features every exchange carries, the servers that accept its
/// requests and its request pipeline, then builds it.
/// </summary>
/// <remarks>
/// <para>
/// The hosting runtime implements this contract (<c>WebApplication.CreateBuilder(args)</c>). Its members
/// take values and factories over the application context, never a service container, so any
/// composition surface that implements it can be handed features and servers.
/// </para>
/// <para>
/// Feature packages do not extend this contract. Their registration verbs are component integrations
/// projected onto the application's service registrations, <c>builder.Services.Add&lt;Feature&gt;(...)</c>,
/// which register the same <see cref="IHttpFeature"/> singleton <see cref="AddFeature(IHttpFeature)"/>
/// does (owner decision 34, #1380). <see cref="AddFeature(IHttpFeature)"/> stays the raw registration
/// path for a feature no package ships a verb for. A request feature is a singleton: the hosting runtime
/// rejects a scoped, transient or disposable one.
/// </para>
/// </remarks>
public interface IWebApplicationBuilder
{
    /// <summary>
    /// Adds a feature that the application exposes on <see cref="IWebApplicationContext.Features"/> and
    /// stamps onto every exchange's <see cref="IHttpContext.Features"/> before any middleware runs.
    /// </summary>
    /// <remarks>
    /// Features are registered per application. When two registrations share a feature type, the one
    /// registered last is the one an exchange carries.
    /// </remarks>
    /// <param name="feature">The feature instance.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="feature"/> is null.</exception>
    IWebApplicationBuilder AddFeature(IHttpFeature feature);

    /// <summary>
    /// Adds a feature created from the application context, exposed and stamped onto every exchange like
    /// one added with <see cref="AddFeature(IHttpFeature)"/>.
    /// </summary>
    /// <remarks>
    /// The factory runs once, the first time the application's features are resolved after
    /// <see cref="Build"/>, so it sees every registration.
    /// </remarks>
    /// <param name="configure">The factory that creates the feature.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The factory returns <see langword="null"/> when the application's features are resolved.
    /// </exception>
    IWebApplicationBuilder AddFeature(Func<IWebApplicationContext, IHttpFeature> configure);

    /// <summary>
    /// Adds a server instance to the application host lifecycle.
    /// </summary>
    /// <remarks>
    /// Servers start in registration order and stop in reverse registration order. A server
    /// implements only the Web root contract; the Hosting runtime supplies its lifecycle adapter.
    /// </remarks>
    /// <param name="server">The server to start and stop with the application.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is null.</exception>
    IWebApplicationBuilder AddServer(IWebApplicationServer server);

    /// <summary>
    /// Adds a server created from the final application context to the host lifecycle.
    /// </summary>
    /// <remarks>
    /// The factory is resolved once for the built application. Servers start in registration
    /// order and stop in reverse registration order.
    /// </remarks>
    /// <param name="server">The factory that creates the server.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The factory returns <see langword="null"/> when the application is built.
    /// </exception>
    IWebApplicationBuilder AddServer(Func<IWebApplicationContext, IWebApplicationServer> server);

    /// <summary>
    /// Replaces the default request pipeline with a prebuilt pipeline.
    /// </summary>
    /// <remarks>
    /// Hosting-owned fixed terminals, when enabled for the application, remain ahead of the
    /// supplied user pipeline.
    /// </remarks>
    /// <param name="pipeline">The application request pipeline.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pipeline"/> is null.</exception>
    IWebApplicationBuilder AddPipeline(IWebApplicationPipeline pipeline);

    /// <summary>
    /// Builds the application from the registrations. A builder builds one application.
    /// </summary>
    /// <returns>The built application, ready to start.</returns>
    /// <exception cref="InvalidOperationException">
    /// The builder has already built an application, or its registrations cannot be composed into one.
    /// </exception>
    IWebApplication Build();
}

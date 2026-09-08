using System;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Hosting;
using Http;


/// <summary>
/// 
/// </summary>
public interface IWebApplicationBuilder 
{

      ///// <summary>
    ///// 
    ///// </summary>
    ///// <typeparam name="TFeature"></typeparam>
    ///// <param name="feature"></param>
    ///// <returns></returns>
    //IWebApplicationBuilder AddFeature<TFeature>(TFeature feature) where TFeature : IHttpFeature;


    /// <summary>
    /// Adds a feature to be used within the HttpContext Feature Collection. <see cref="IHttpContext.Features"/>
    /// </summary>
    /// <param name="feature"></param>
    /// <returns></returns>
    IWebApplicationBuilder AddFeature(IHttpFeature feature);

    /// <summary>
    ///
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    IWebApplicationBuilder AddFeature(Func<IWebApplicationContext, IHttpFeature> configure);

    /// <summary>
    /// Adds a lifecycle service to the application.
    /// </summary>
    /// <remarks>
    /// Lifecycle services start in registration order before every Web server and stop in
    /// reverse order after every Web server has stopped.
    /// </remarks>
    /// <param name="service">The lifecycle service to add.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    IWebApplicationBuilder AddService(IHostService service);

    /// <summary>
    /// Adds a lifecycle service created from the final application context.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once when the application is built. Lifecycle services start in
    /// registration order before every Web server and stop in reverse order after every Web
    /// server has stopped.
    /// </remarks>
    /// <param name="factory">The factory that creates the lifecycle service.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The factory returns <see langword="null"/> when the application is built.
    /// </exception>
    IWebApplicationBuilder AddService(Func<IWebApplicationContext, IHostService> factory);

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
    /// 
    /// </summary>
    /// <returns></returns>
    IWebApplication Build();
}

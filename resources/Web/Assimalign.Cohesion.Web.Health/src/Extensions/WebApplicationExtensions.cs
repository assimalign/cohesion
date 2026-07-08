using System;

namespace Assimalign.Cohesion.Web.Health;

using Assimalign.Cohesion.Health;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Health.Internal;

/// <summary>
/// Pipeline-builder members that map health endpoints (<c>/healthz</c>, <c>/livez</c>,
/// <c>/readyz</c>) onto an <see cref="IHealthCheckService"/>.
/// </summary>
/// <remarks>
/// The <see cref="IHealthCheckService"/> is supplied explicitly and resolved at build time — the
/// middleware never performs request-time service location. In a hosted Web application, resolve
/// it from the application's services after <c>Build()</c>:
/// <code>
/// builder.Services.AddHealthChecks().AddCheck("db", ...);
/// WebApplication app = builder.Build();
/// app.MapHealthChecks(app.Context.ServiceProvider.GetRequiredService&lt;IHealthCheckService&gt;());
/// </code>
/// </remarks>
public static class WebApplicationExtensions
{
    private static readonly HttpPath DefaultHealthPath = new("/healthz");
    private static readonly HttpPath DefaultReadinessPath = new("/readyz");
    private static readonly HttpPath DefaultLivenessPath = new("/livez");

    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Maps a health endpoint at <paramref name="path"/>. A matching <c>GET</c>/<c>HEAD</c>
        /// request runs the selected checks, maps the aggregate status to a code (200/503 by
        /// default), attaches an <see cref="IHttpHealthFeature"/>, writes the report, and
        /// short-circuits the pipeline. Any other request is passed through.
        /// </summary>
        /// <param name="path">The endpoint path.</param>
        /// <param name="service">The health-check service the endpoint evaluates.</param>
        /// <param name="configure">An optional callback to configure the endpoint.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="service"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder MapHealthChecks(
            HttpPath path,
            IHealthCheckService service,
            Action<HealthEndpointOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(service);

            var options = new HealthEndpointOptions();
            configure?.Invoke(options);
            ArgumentNullException.ThrowIfNull(options.ResponseWriter);

            return builder.Use(async (context, next) =>
            {
                if (!IsHealthRequest(context.Request, path))
                {
                    await next.Invoke(context);
                    return;
                }

                HealthReport report = await service.CheckHealthAsync(options.Predicate, context.RequestCancelled);

                context.Features.Set(new HttpHealthFeature(report));
                context.Response.StatusCode = options.StatusCodeFor(report.Status);

                if (!options.AllowCachingResponses)
                {
                    context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store, no-cache";
                }

                await options.ResponseWriter.WriteAsync(context, report, context.RequestCancelled);

                // Short-circuit: a health endpoint is terminal — do not invoke downstream middleware.
            });
        }

        /// <summary>
        /// Maps a health endpoint at the default <c>/healthz</c> path.
        /// </summary>
        /// <param name="service">The health-check service the endpoint evaluates.</param>
        /// <param name="configure">An optional callback to configure the endpoint.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        public IWebApplicationPipelineBuilder MapHealthChecks(
            IHealthCheckService service,
            Action<HealthEndpointOptions>? configure = null)
            => builder.MapHealthChecks(DefaultHealthPath, service, configure);

        /// <summary>
        /// Maps a readiness endpoint (default <c>/readyz</c>) that runs only checks tagged
        /// <see cref="HealthTags.Ready"/> — the dependencies that must be up before traffic is routed.
        /// </summary>
        /// <param name="service">The health-check service the endpoint evaluates.</param>
        /// <param name="path">The endpoint path, or <see langword="null"/> for <c>/readyz</c>.</param>
        /// <param name="configure">An optional callback to further configure the endpoint.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        public IWebApplicationPipelineBuilder MapReadinessCheck(
            IHealthCheckService service,
            HttpPath? path = null,
            Action<HealthEndpointOptions>? configure = null)
            => builder.MapHealthChecks(path ?? DefaultReadinessPath, service, options =>
            {
                options.Predicate = HealthCheckPredicates.Ready;
                configure?.Invoke(options);
            });

        /// <summary>
        /// Maps a liveness endpoint (default <c>/livez</c>) that runs only checks tagged
        /// <see cref="HealthTags.Live"/>. With no live-tagged checks the endpoint reports the process
        /// as up (an empty report is <see cref="HealthStatus.Healthy"/>).
        /// </summary>
        /// <param name="service">The health-check service the endpoint evaluates.</param>
        /// <param name="path">The endpoint path, or <see langword="null"/> for <c>/livez</c>.</param>
        /// <param name="configure">An optional callback to further configure the endpoint.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        public IWebApplicationPipelineBuilder MapLivenessCheck(
            IHealthCheckService service,
            HttpPath? path = null,
            Action<HealthEndpointOptions>? configure = null)
            => builder.MapHealthChecks(path ?? DefaultLivenessPath, service, options =>
            {
                options.Predicate = HealthCheckPredicates.Live;
                configure?.Invoke(options);
            });
    }

    private static bool IsHealthRequest(IHttpRequest request, HttpPath path)
        => request.Path.Equals(path) && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head);
}

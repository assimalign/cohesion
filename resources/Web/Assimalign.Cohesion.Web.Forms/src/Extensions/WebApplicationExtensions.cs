namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;

/// <summary>
/// Pipeline-builder extensions that wire HTTP form parsing into the Web
/// application middleware pipeline.
/// </summary>
public static class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds middleware that installs an <see cref="IHttpFormFeature"/> on each
        /// request (when one is not already present) and eagerly parses the form
        /// body so downstream middleware can read <c>context.Request.Form</c>
        /// synchronously.
        /// </summary>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <remarks>
        /// The parse runs for every request regardless of Content-Type; bodies
        /// that are neither <c>application/x-www-form-urlencoded</c> nor
        /// <c>multipart/form-data</c> yield an empty collection. Middleware that
        /// only needs the form on specific routes can skip this and call
        /// <c>context.ReadFormAsync(...)</c> lazily instead.
        /// </remarks>
        public IWebApplicationPipelineBuilder UseForms()
        {
            return builder.Use(async (context, next) =>
            {
                IHttpFeatureCollection features = context.Features;
                IHttpFormFeature? feature = features.Get<IHttpFormFeature>();

                if (feature is null)
                {
                    feature = new HttpFormFeature(context.Request);
                    features.Set<IHttpFormFeature>(feature);
                }

                await feature.ReadFormAsync(context.RequestCancelled);
                await next.Invoke(context);
            });
        }
    }
}

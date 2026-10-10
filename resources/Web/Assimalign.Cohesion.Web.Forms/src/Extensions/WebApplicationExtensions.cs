namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Internal;

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
        /// <para>
        /// The parse runs for every request regardless of Content-Type; bodies
        /// that are neither <c>application/x-www-form-urlencoded</c> nor
        /// <c>multipart/form-data</c> yield an empty collection. Middleware that
        /// only needs the form on specific routes can skip this and call
        /// <c>context.ReadFormAsync(...)</c> lazily instead.
        /// </para>
        /// <para>
        /// A form the parse rejects is answered here and the rest of the pipeline
        /// does not run: <c>413 Content Too Large</c> when the body exceeds a
        /// configured <see cref="HttpFormOptions"/> limit, and <c>400 Bad Request</c>
        /// when it is malformed, both as <c>application/problem+json</c> with the
        /// payload a form-bound endpoint writes for the same failure.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseForms()
        {
            return builder.Use(new FormsMiddleware());
        }
    }
}

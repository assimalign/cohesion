using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Query.Internal;

namespace Assimalign.Cohesion.Web.Query;

/// <summary>
/// Configures the QUERY request-validation middleware that
/// <c>builder.Services.AddQueryValidation(validation =&gt; ...)</c> registers and
/// <see cref="WebApplicationExtensions.UseQueryValidation(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// <see cref="Build"/> snapshots <see cref="Options"/> into the middleware — the accepted set becomes an
/// immutable <c>Accept-Query</c> value serialized once, the response set an array — so changing the
/// options afterwards reaches only a later <see cref="Build"/>, never the middleware already registered.
/// The <see cref="WebQueryValidationOptions.InvalidContentTypeStatusCode"/> setter validates its status
/// when it is assigned, so a bad policy fails inside the registration callback.
/// </remarks>
public sealed class WebQueryValidationBuilder
{
    /// <summary>
    /// Initializes a builder with the defaults: any syntactically valid <c>Content-Type</c> is accepted,
    /// no response negotiation is performed, and query content whose <c>Content-Type</c> is missing or
    /// malformed is rejected with <c>400 Bad Request</c>.
    /// </summary>
    public WebQueryValidationBuilder()
    {
    }

    /// <summary>
    /// Gets the validation options: the resource's accepted query formats (its <c>Accept-Query</c> set),
    /// the representations it can produce, the status for a missing or malformed <c>Content-Type</c>,
    /// and whether the accepted set is advertised.
    /// </summary>
    public WebQueryValidationOptions Options { get; } = new();

    /// <summary>
    /// Builds the QUERY request-validation middleware from a snapshot of <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which enforces the RFC 10008 &#167; 2.1 / &#167; 2.3 request-content rules on
    /// QUERY requests (400 or 415 for a missing, malformed, or unaccepted <c>Content-Type</c>, 406 for
    /// an unsatisfiable <c>Accept</c>) and passes every other request through untouched.
    /// </returns>
    public IWebApplicationMiddleware Build()
    {
        return new WebQueryValidationMiddleware(Options);
    }
}

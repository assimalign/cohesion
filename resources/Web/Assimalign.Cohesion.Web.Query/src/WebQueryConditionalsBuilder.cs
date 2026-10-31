using System;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Query.Internal;

namespace Assimalign.Cohesion.Web.Query;

/// <summary>
/// Configures the conditional-QUERY middleware that
/// <c>builder.Services.AddQueryConditionals(conditionals =&gt; conditionals.UseResourceValidators(...))</c>
/// registers and <see cref="WebApplicationExtensions.UseQueryConditionals(IWebApplicationPipelineBuilder)"/>
/// places.
/// </summary>
/// <remarks>
/// The middleware evaluates a QUERY's preconditions against the target resource's current validators
/// (RFC 10008 &#167; 2.6), which only the application can resolve. It therefore has no default:
/// <see cref="Build"/> refuses to run until <see cref="UseResourceValidators"/> supplies the provider,
/// so a registration without one fails where the application registers it.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("WebQueryConditionalsBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class WebQueryConditionalsBuilder
{
    private WebQueryResourceValidatorsProvider? _resourceValidators;

    /// <summary>
    /// Initializes a builder with no provider of the resource's validators, which <see cref="Build"/>
    /// refuses until <see cref="UseResourceValidators"/> supplies one.
    /// </summary>
    public WebQueryConditionalsBuilder()
    {
    }

    /// <summary>
    /// Resolves the target resource's current validators through <paramref name="resourceValidators"/>
    /// before a QUERY executes, so its precondition fields are evaluated exactly as for the equivalent
    /// conditional GET.
    /// </summary>
    /// <param name="resourceValidators">
    /// Resolves the target resource's current validators: the ETag and Last-Modified of the representation
    /// the equivalent GET would select. It returns <see langword="null"/> when they are unknown for the
    /// request, which then passes through without precondition evaluation.
    /// </param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resourceValidators"/> is <see langword="null"/>.</exception>
    /// <remarks>A later call replaces the provider an earlier call supplied.</remarks>
    public WebQueryConditionalsBuilder UseResourceValidators(WebQueryResourceValidatorsProvider resourceValidators)
    {
        ArgumentNullException.ThrowIfNull(resourceValidators);

        _resourceValidators = resourceValidators;
        return this;
    }

    /// <summary>
    /// Builds the conditional-QUERY middleware over the provider <see cref="UseResourceValidators"/> supplied.
    /// </summary>
    /// <returns>
    /// The middleware, which answers a QUERY whose preconditions resolve the exchange with <c>304 Not
    /// Modified</c> or <c>412 Precondition Failed</c> without executing it, stamps the validators onto the
    /// response of a QUERY that proceeds, and passes every other request through untouched.
    /// </returns>
    /// <exception cref="InvalidOperationException"><see cref="UseResourceValidators"/> was not called.</exception>
    public IWebApplicationMiddleware Build()
    {
        WebQueryResourceValidatorsProvider resourceValidators = _resourceValidators
            ?? throw new InvalidOperationException(ErrorMessages.QueryResourceValidatorsNotSet);

        return new WebQueryConditionalMiddleware(resourceValidators);
    }

    private sealed class DebugView
    {
        public DebugView(WebQueryConditionalsBuilder value)
        {
        }
    }
}

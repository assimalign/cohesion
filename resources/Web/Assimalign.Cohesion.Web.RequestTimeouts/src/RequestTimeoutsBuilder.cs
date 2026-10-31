using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.RequestTimeouts.Internal;

namespace Assimalign.Cohesion.Web.RequestTimeouts;

/// <summary>
/// Configures the request-timeout middleware that
/// <c>builder.Services.AddRequestTimeouts(timeouts =&gt; ...)</c> registers and
/// <see cref="WebApplicationExtensions.UseRequestTimeouts(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// <para>
/// The options validate themselves as they are set: a <see cref="RequestTimeoutPolicy"/> refuses a
/// zero or negative timeout when it is created, and <see cref="RequestTimeoutOptions.TimeProvider"/>
/// refuses <see langword="null"/>. <see cref="Build"/> snapshots them, so changing the options
/// afterwards has no effect on the middleware it returned.
/// </para>
/// <para>
/// The global default policy (<see cref="RequestTimeoutOptions.DefaultPolicy"/>) governs every request
/// the middleware sees unless the endpoint <c>UseRouting</c> published carries its own
/// <see cref="RequestTimeoutMetadata"/>. Without one, only such endpoints are governed.
/// </para>
/// </remarks>
public sealed class RequestTimeoutsBuilder
{
    /// <summary>
    /// Initializes a builder with the defaults: no global timeout, the system time provider, and
    /// enforcement suspended while a debugger is attached.
    /// </summary>
    public RequestTimeoutsBuilder()
    {
    }

    /// <summary>
    /// Gets the middleware's options: the global default policy, the time provider the timers measure
    /// against, and whether enforcement is suspended while a debugger is attached.
    /// </summary>
    public RequestTimeoutOptions Options { get; } = new();

    /// <summary>
    /// Builds the request-timeout middleware from a snapshot of <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which arms a per-exchange timer with the endpoint's policy or the global default,
    /// cancels downstream work when it fires, and answers the timed-out request with the policy's status
    /// (or aborts it cleanly at the protocol layer when the response has already started).
    /// </returns>
    public IWebApplicationMiddleware Build()
    {
        return new RequestTimeoutMiddleware(Options);
    }
}

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Per-exchange session state stored in <see cref="IHttpContext.Features"/>.
/// </summary>
/// <remarks>
/// <para>
/// The protocol core deliberately omits a <c>Session</c> property &#8211; HTTP sessions
/// are an application-layer concept, not part of the wire protocol. The
/// <c>Assimalign.Cohesion.Http.Sessions</c> package layers session state on top of
/// the protocol core by attaching this feature to
/// <see cref="IHttpContext.Features"/>. Consumers prefer the
/// <see cref="HttpContextSessionExtensions.Session"/> /
/// <see cref="HttpContextSessionExtensions.RequireSession"/> extension properties on
/// <see cref="IHttpContext"/>; middleware that needs a richer feature implementation
/// can install one directly via
/// <c>context.Features.Set&lt;IHttpSessionFeature&gt;(...)</c>.
/// </para>
/// </remarks>
public interface IHttpSessionFeature
{
    /// <summary>
    /// Gets or sets the session attached to the current exchange. Never
    /// <see langword="null"/> once the feature is installed.
    /// </summary>
    IHttpSession Session { get; set; }
}

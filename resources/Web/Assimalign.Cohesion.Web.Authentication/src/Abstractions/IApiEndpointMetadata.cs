namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// Endpoint metadata marker declaring that an endpoint is an <em>API</em> endpoint: on an
/// authentication challenge or forbid it should receive a bare <c>401</c>/<c>403</c> status
/// rather than an interactive redirect to a login or access-denied page.
/// </summary>
/// <remarks>
/// <para>
/// The cookie handler resolves this marker from the matched route's endpoint metadata (the
/// reflection-free <c>#150</c> metadata bag surfaced by <c>Assimalign.Cohesion.Web.Routing</c>).
/// When present, the handler suppresses its redirect behavior and emits status codes — mirroring
/// the .NET 10 cookie handler's <c>IApiEndpointMetadata</c>-keyed decision, so a browser endpoint
/// and a JSON API endpoint sharing one cookie scheme each get the challenge shape their client
/// expects.
/// </para>
/// <para>
/// The marker is an empty interface: attach any instance implementing it (typically the shared
/// <see cref="ApiEndpointMetadata.Instance"/>) to an endpoint's metadata to opt that endpoint into
/// non-redirecting behavior. It carries no data because the decision it drives is binary.
/// </para>
/// </remarks>
public interface IApiEndpointMetadata
{
}

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Carries the antiforgery service on an exchange, so the handler that mints tokens and the code that
/// validates them resolve the same configured service (and therefore the same protector) through
/// <c>context.Antiforgery</c> / <c>context.RequireAntiforgery</c>.
/// </summary>
/// <remarks>
/// Implementations report <c>nameof(IHttpAntiforgeryFeature)</c> as their
/// <see cref="IHttpFeature.Name"/>. The feature collection is name-keyed, so one exchange carries at most
/// one antiforgery service: assigning <c>context.Antiforgery</c> replaces an application-level
/// registration (for example the one <c>AddAntiforgery</c> in <c>Assimalign.Cohesion.Web.Antiforgery</c>
/// seeds onto every exchange) rather than shadowing it.
/// </remarks>
public interface IHttpAntiforgeryFeature : IHttpFeature
{
    /// <summary>
    /// Gets the antiforgery service attached to the exchange.
    /// </summary>
    IHttpAntiforgery Antiforgery { get; }
}

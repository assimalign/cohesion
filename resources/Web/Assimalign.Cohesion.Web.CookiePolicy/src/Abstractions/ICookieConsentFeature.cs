using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.CookiePolicy;

/// <summary>
/// The per-exchange consent state the cookie policy installs on <see cref="IHttpContext.Features"/>:
/// whether the request needs consent before non-essential cookies are issued, whether it has consent,
/// and the verbs that grant or withdraw it.
/// </summary>
/// <remarks>
/// <para>
/// Resolve it with <c>context.Features.Get&lt;ICookieConsentFeature&gt;()</c>. It is present on every
/// exchange that <c>UseCookiePolicy</c> runs for, and absent otherwise.
/// </para>
/// <para>
/// Consent is recorded in a cookie (<see cref="CookiePolicyOptions.ConsentCookieName"/>).
/// <see cref="HasConsent"/> starts from that cookie on the request, and <see cref="GrantConsent"/> and
/// <see cref="WithdrawConsent"/> change it for the rest of the exchange and append the cookie that
/// records the change on the response. The policy judges a cookie when it is appended, so granting
/// consent lets through the non-essential cookies appended after the grant, and withdrawing it does not
/// recall the ones already appended.
/// </para>
/// <para>
/// The policy consults the exchange's consent feature only through <see cref="CanTrack"/>, and it
/// installs its own implementation only when the exchange has none. An application that keeps consent
/// elsewhere, such as a consent-management platform's signal or the <c>Sec-GPC</c> request header, can
/// install its own implementation before <c>UseCookiePolicy</c> runs. The policy then honors that
/// implementation, and <see cref="CookiePolicyOptions.CheckConsentNeeded"/> and the consent cookie
/// options do not apply to the exchange.
/// </para>
/// <para>
/// The feature is exchange-scoped and not thread-safe; use it from the exchange's own handling flow.
/// </para>
/// </remarks>
public interface ICookieConsentFeature : IHttpFeature
{
    /// <summary>
    /// Gets whether this request needs consent before non-essential cookies are issued: the result of
    /// <see cref="CookiePolicyOptions.CheckConsentNeeded"/>, evaluated once per exchange.
    /// <see langword="false"/> when no predicate is configured.
    /// </summary>
    bool IsConsentNeeded { get; }

    /// <summary>
    /// Gets whether the user has consented: the request carried the consent cookie with
    /// <see cref="CookiePolicyOptions.ConsentCookieValue"/>, or <see cref="GrantConsent"/> was called
    /// during this exchange, and <see cref="WithdrawConsent"/> was not called after it.
    /// </summary>
    bool HasConsent { get; }

    /// <summary>
    /// Gets whether the policy issues non-essential cookies for this exchange:
    /// <see langword="true"/> when consent is not needed or has been given.
    /// </summary>
    bool CanTrack { get; }

    /// <summary>
    /// Records the user's consent. Appends the consent cookie, unless the exchange already has consent,
    /// and lets non-essential cookies appended afterwards in this exchange through.
    /// </summary>
    /// <remarks>
    /// Once the response head has been committed no cookie can be appended; the consent state still
    /// changes for the rest of the exchange.
    /// </remarks>
    void GrantConsent();

    /// <summary>
    /// Withdraws the user's consent. Appends a deletion of the consent cookie, when the exchange had
    /// consent, and suppresses non-essential cookies appended afterwards in this exchange.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Withdrawal removes only the consent record. The application deletes the non-essential cookies it
    /// issued earlier; a deletion always passes the policy.
    /// </para>
    /// <para>
    /// Once the response head has been committed no cookie can be appended; the consent state still
    /// changes for the rest of the exchange.
    /// </para>
    /// </remarks>
    void WithdrawConsent();
}

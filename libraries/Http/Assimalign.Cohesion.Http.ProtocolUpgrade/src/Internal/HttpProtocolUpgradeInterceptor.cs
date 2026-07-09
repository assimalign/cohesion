using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The exchange interceptor that makes HTTP/1.1 protocol upgrades and <c>CONNECT</c> tunnelling
/// available on an exchange. One stateless instance participates in both phases:
/// </summary>
/// <remarks>
/// <list type="number">
///   <item><description><see cref="AfterRequestHead"/> detects the RFC 9110 §7.8 upgrade signal
///   (<c>Connection: upgrade</c> token <b>and</b> a non-empty <c>Upgrade</c> header) or the
///   §9.3.6 <c>CONNECT</c> shape on the parsed head, and records it as an internal
///   <see cref="HttpProtocolUpgradeCandidate"/> feature.</description></item>
///   <item><description><see cref="BeforeResponse"/> consumes the candidate and, when the
///   transport's exchange control can surrender the connection
///   (<see cref="HttpResponseInterceptorContext.Control"/> with
///   <see cref="IHttpExchangeControl.CanTakeOver"/>), installs the public
///   <see cref="IHttpProtocolUpgradeFeature"/> wrapping an <see cref="Http1ProtocolUpgrade"/> —
///   the object <c>context.Upgrade</c> surfaces to the application.</description></item>
/// </list>
/// <para>
/// Detection is HTTP/1.1-only by design: HTTP/2 and HTTP/3 removed the <c>Upgrade</c> mechanism
/// (RFC 9113 §8.6, RFC 9114 §4.2), and their <c>CONNECT</c> shapes (including extended CONNECT)
/// are per-stream semantics over a shared connection — there is no whole-connection transition
/// to model, so ordinary and multiplexed exchanges alike surface <c>context.Upgrade == null</c>.
/// </para>
/// <para>
/// Both hooks are CPU-only (token scanning over already-parsed headers) per the interceptor
/// contract, and all per-request state lives in the exchange's feature collection, never in
/// instance fields.
/// </para>
/// </remarks>
internal sealed class HttpProtocolUpgradeInterceptor : HttpExchangeInterceptor
{
    /// <inheritdoc />
    public override void AfterRequestHead(HttpRequestInterceptorContext context)
    {
        if (context.Version != HttpVersion.Http11)
        {
            return;
        }

        if (context.Method == HttpMethod.Connect)
        {
            // The transport enforces CONNECT ⇒ authority-form at parse time (HttpRequestTarget's
            // method/form pairing), so the method alone identifies a tunnel request here.
            // RFC 9110 §7.8 requires a server to ignore an Upgrade header on CONNECT, hence the
            // return before upgrade detection.
            context.Features.Set(new HttpProtocolUpgradeCandidate(HttpProtocolUpgradeKind.Connect, protocol: null));
            return;
        }

        // RFC 9110 §7.8 — an upgrade is signalled by BOTH a "Connection: upgrade" token AND a
        // non-empty Upgrade header naming the target protocol; a bare Upgrade header without the
        // Connection token is not actionable.
        if (HasConnectionUpgradeToken(context.Headers)
            && context.Headers.TryGetValue(HttpHeaderKey.Upgrade, out HttpHeaderValue upgradeValue)
            && FirstToken(upgradeValue.Value) is { } protocol)
        {
            context.Features.Set(new HttpProtocolUpgradeCandidate(HttpProtocolUpgradeKind.Upgrade, protocol));
        }
    }

    /// <inheritdoc />
    public override void BeforeResponse(HttpResponseInterceptorContext context)
    {
        HttpProtocolUpgradeCandidate? candidate = context.Features.Get<HttpProtocolUpgradeCandidate>();
        if (candidate is null)
        {
            return;
        }

        // The marker's job ends here; remove it so only the public feature (if any) remains
        // visible on the exchange.
        context.Features.Remove(HttpProtocolUpgradeCandidate.FeatureName);

        // Defensive: the HTTP/1.1 exchange control can always surrender its connection, but an
        // exchange whose control cannot (HTTP/2 / HTTP/3 multiplexed streams, or a hand-built
        // context with no control) degrades to "no upgrade available" rather than surfacing a
        // feature whose accept could never work.
        if (context.Control is not { CanTakeOver: true } control)
        {
            return;
        }

        Http1ProtocolUpgrade upgrade = new(control, context.Headers, context.Features, candidate.Kind, candidate.Protocol);
        context.Features.Set(new HttpProtocolUpgradeFeature(upgrade));
    }

    /// <summary>
    /// Returns whether the <c>Connection</c> header lists the <c>upgrade</c> token
    /// (case-insensitive, comma-list aware, across repeated field lines).
    /// </summary>
    private static bool HasConnectionUpgradeToken(HttpHeaderCollection headers)
    {
        if (!headers.TryGetValue(HttpHeaderKey.Connection, out HttpHeaderValue value))
        {
            return false;
        }

        foreach (string? entry in value)
        {
            if (entry is null)
            {
                continue;
            }

            foreach (string segment in entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(segment, "upgrade", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the first comma-delimited token of an <c>Upgrade</c> header value, trimmed, or
    /// <see langword="null"/> when the value is empty. RFC 9110 §7.8 lists protocols in
    /// preference order and a successful 101 names the single protocol the server switches to,
    /// so the first (most-preferred) token is what the upgrade surfaces.
    /// </summary>
    private static string? FirstToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        int comma = value.IndexOf(',');
        string token = (comma < 0 ? value : value[..comma]).Trim();
        return token.Length == 0 ? null : token;
    }
}

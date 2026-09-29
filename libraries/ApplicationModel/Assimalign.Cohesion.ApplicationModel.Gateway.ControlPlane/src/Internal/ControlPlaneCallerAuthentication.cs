using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

/// <summary>
/// The control plane's caller-authentication pipeline: the built-in ES256 trusted-issuer
/// authenticator first, then each application-registered <see cref="IApplicationCallerAuthenticator"/>
/// in registration order.
/// </summary>
/// <remarks>
/// The built-in authenticator only ever authenticates a <c>Bearer</c> credential or passes: a
/// credential it cannot verify - another scheme, another issuer, or an invalid application-key
/// token - falls through, so a registered authenticator can accept an identity provider's token.
/// A registered authenticator's <see cref="ApplicationCallerStatus.NoResult"/> falls through as well;
/// the first <see cref="ApplicationCallerStatus.Authenticated"/>,
/// <see cref="ApplicationCallerStatus.Unauthorized"/>, or <see cref="ApplicationCallerStatus.Forbidden"/>
/// is final. When every authenticator passes the result is <see cref="ApplicationCallerStatus.NoResult"/>,
/// which the server answers exactly as it answered an unverifiable credential before registered
/// authenticators existed. An undefined status, or an <see cref="ApplicationCallerStatus.Authenticated"/>
/// result without a caller, is a defect that throws <see cref="InvalidOperationException"/>.
/// </remarks>
internal static class ControlPlaneCallerAuthentication
{
    private const string bearerScheme = "Bearer";

    public static async ValueTask<ApplicationCallerResult> AuthenticateAsync(
        ApplicationName application,
        string scheme,
        string credential,
        IReadOnlyList<TrustedIssuer> trustedIssuers,
        IReadOnlyList<IApplicationCallerAuthenticator> authenticators,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.Equals(scheme, bearerScheme, StringComparison.OrdinalIgnoreCase) &&
            ControlPlaneTokenVerifier.TryVerify(credential, trustedIssuers, now, out ControlPlanePrincipal principal))
        {
            return new ApplicationCallerResult(ApplicationCallerStatus.Authenticated, ToCaller(principal), null);
        }

        if (authenticators.Count == 0)
        {
            return default;
        }

        var request = new ApplicationCallerRequest(application, scheme, credential);
        for (int index = 0; index < authenticators.Count; index++)
        {
            ApplicationCallerResult result = await authenticators[index]
                .AuthenticateAsync(request, trustedIssuers, cancellationToken)
                .ConfigureAwait(false);
            switch (result.Status)
            {
                case ApplicationCallerStatus.NoResult:
                    continue;
                case ApplicationCallerStatus.Authenticated when result.Caller is null:
                    // The record's constructor refuses this, but a 'with' expression on a default
                    // result does not. Without a caller there is no identity to authorize, so it is
                    // a defect (500), never an admission.
                    throw new InvalidOperationException(
                        $"Caller authenticator '{authenticators[index].GetType().Name}' returned " +
                        "Authenticated without an ApplicationCaller.");
                case ApplicationCallerStatus.Authenticated:
                case ApplicationCallerStatus.Unauthorized:
                case ApplicationCallerStatus.Forbidden:
                    return result;
                default:
                    throw new InvalidOperationException(
                        $"Caller authenticator '{authenticators[index].GetType().Name}' returned undefined " +
                        $"status '{result.Status}'.");
            }
        }

        return default;
    }

    // The built-in authenticator's caller: the token's issuer is the calling application, and the
    // gateway token-use claim is what makes it a peer gateway rather than a read-only developer.
    // The matched trusted issuer's command-kind restriction travels with the caller.
    public static ApplicationCaller ToCaller(ControlPlanePrincipal principal) =>
        new(
            ApplicationName.Parse(principal.Issuer),
            principal.Subject,
            principal.CanDispatchCommands ? ApplicationCallerKind.Peer : ApplicationCallerKind.Developer,
            principal.AllowedCommandKinds);

    // Command routes need a peer gateway acting for a named application: the owner of every
    // command it sends or reads is that application.
    public static bool CanDispatchCommands(ApplicationCaller caller) =>
        caller.Kind == ApplicationCallerKind.Peer && caller.Application is not null;
}
